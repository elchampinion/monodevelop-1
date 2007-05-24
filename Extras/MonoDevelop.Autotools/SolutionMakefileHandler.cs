/*
Copyright (C) 2006  Matthias Braun <matze@braunis.de>
					Scott Ellington <scott.ellington@gmail.com>
 
This library is free software; you can redistribute it and/or
modify it under the terms of the GNU Lesser General Public
License as published by the Free Software Foundation; either
version 2 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Lesser General Public License for more details.

You should have received a copy of the GNU Lesser General Public
License along with this library; if not, write to the
Free Software Foundation, Inc., 59 Temple Place - Suite 330,
Boston, MA 02111-1307, USA.
*/

using System;
using System.IO;
using System.Text;
using System.Collections;
using MonoDevelop.Projects;
using MonoDevelop.Core;

namespace MonoDevelop.Autotools
{
	public class SolutionMakefileHandler : IMakefileHandler
	{
		// Recurses into children and tests if they are deployable.
		public bool CanDeploy ( CombineEntry entry )
		{
			return entry is Combine;
		}

		public Makefile Deploy ( AutotoolsContext ctx, CombineEntry entry, IProgressMonitor monitor )
		{
			monitor.BeginTask ( GettextCatalog.GetString  ("Creating Makefile.am for Solution {0}", entry.Name), 1 );			
			Makefile mfile = new Makefile ();

			try
			{
				if ( !CanDeploy ( entry ) )
					throw new Exception ( GettextCatalog.GetString ("Not a deployable solution.") );

				Combine combine = entry as Combine;

				StringBuilder subdirs = new StringBuilder();
				subdirs.Append ("#Warning: This is an automatically generated file, do not edit!\n");

				ArrayList children = new ArrayList ();
				foreach ( CombineConfiguration config in combine.Configurations )
				{
					if ( !ctx.IsSupportedConfiguration ( config.Name ) ) continue;
					
					subdirs.AppendFormat ( "if {0}\n", "ENABLE_" + ctx.EscapeAndUpperConfigName (config.Name));
					subdirs.Append (" SUBDIRS = ");
					
					foreach ( CombineEntry ce in CalculateSubDirOrder ( config ) )
					{
						if (combine.BaseDirectory == ce.BaseDirectory) {
							subdirs.Append (" . ");
						} else {
							if ( !ce.BaseDirectory.StartsWith (combine.BaseDirectory) )
								throw new Exception ( GettextCatalog.GetString (
									"Child projects / solutions must be in sub-directories of their parent") );
							
							// add the subdirectory to the list
							string path = Path.GetDirectoryName (ce.RelativeFileName);
							if (path.StartsWith ("." + Path.DirectorySeparatorChar) )
								path = path.Substring (2);
							subdirs.Append (" ");
							subdirs.Append ( AutotoolsContext.EscapeStringForAutomake (path) );
						}

						if (!children.Contains (ce))
							children.Add ( ce );
					}
					subdirs.Append ( "\nendif\n" );
				}
				mfile.Append ( subdirs.ToString () );

				string includedProject = null;

				// deploy recursively
				foreach ( CombineEntry ce in children )
				{
					IMakefileHandler handler = AutotoolsContext.GetMakefileHandler ( ce );
					Makefile makefile;
					if ( handler != null && handler.CanDeploy ( ce ) )
					{
						makefile = handler.Deploy ( ctx, ce, monitor );
						if (combine.BaseDirectory == ce.BaseDirectory) {
							if (includedProject != null)
								throw new Exception ( GettextCatalog.GetString (
									"More than 1 project in the same directory as the top-level solution is not supported."));

							// project is in the solution directory
							includedProject = String.Format ("include {0}.make", ce.Name);
							continue;
						}

						string outpath = Path.Combine(Path.GetDirectoryName(ce.FileName), "Makefile");
						StreamWriter writer = new StreamWriter ( outpath + ".am" );
						makefile.Write ( writer );
						writer.Close ();
						ctx.AddAutoconfFile ( outpath );
					}
					else {
						monitor.Log .WriteLine("Project '{0}' skipped.", ce.Name); 
					}
				}
				mfile.Append (GettextCatalog.GetString ("# Include project specific makefile"));
				mfile.Append (includedProject);

				monitor.Step (1);
			}
			finally
			{
				monitor.EndTask ();
			}
			return mfile;
		}

		// utility function for finding the correct order to process directories
		ArrayList CalculateSubDirOrder ( CombineConfiguration config )
		{
			ArrayList resultOrder = new ArrayList();
			Set dependenciesMet = new Set();
			Set inResult = new Set();

			bool added;
			string notMet;
			do 
			{
				added = false;
				notMet = null;

				foreach (CombineConfigurationEntry centry in config.Entries) 
				{
					if ( !centry.Build ) continue;
					
					CombineEntry entry = centry.Entry;
					
					if ( inResult.Contains (entry) ) continue;

					Set references, provides;
					if (entry is Project)
					{
						Project project = entry as Project;

						references = GetReferencedProjects (project);
						provides = new Set();
						provides.Add(project.Name);
					} 
					else if (entry is Combine) 
					{
						CombineConfiguration cc = (entry as Combine).Configurations[config.Name] as CombineConfiguration;
						if ( cc == null ) continue;
						GetAllProjects ( cc, out provides, out references);
					}
					else continue;

					if (dependenciesMet.ContainsSet (references) ) 
					{
						resultOrder.Add (entry);
						dependenciesMet.Union(provides);
						inResult.Add(entry);
						added = true;
					} 
					else notMet = entry.Name;
				}
			} while (added == true);

			if (notMet != null) 
				throw new Exception("Impossible to find a solution order that satisfies project references for '" + notMet + "'");

			return resultOrder;
		}

		// cache references
		Hashtable projectReferences = new Hashtable();		
		/**
		 * returns a set of all monodevelop projects that a give
		 * projects references
		 */
		Set GetReferencedProjects (Project project)
		{
			Set set = (Set) projectReferences [project];
			if (set != null) return set;

			set = new Set();

			foreach (ProjectReference reference in project.ProjectReferences) 
			{
				if (reference.ReferenceType == ReferenceType.Project)
					set.Add (reference.Reference);
			}

			projectReferences[project] = set;
			return set;
		}

		// cache references
		Hashtable combineProjects = new Hashtable();
		Hashtable combineReferences = new Hashtable();
		/**
		 * returns a set of projects that a combine contains and a set of projects
		 * that are referenced from combine projects but not part of the combine
		 */
		void GetAllProjects (CombineConfiguration config, out Set projects, out Set references)
		{
			projects = (Set) combineProjects [config];
			if(projects != null) 
			{
				references = (Set) combineReferences [config];
				return;
			}

			projects = new Set();
			references = new Set();
			
			foreach (CombineConfigurationEntry centry in config.Entries) 
			{
				if ( !centry.Build ) continue;
				
				CombineEntry entry = centry.Entry;
				if (entry is Project) 
				{
					Project project = entry as Project;
					projects.Add (project.Name);
					references.Union ( GetReferencedProjects (project) );
				} 
				else if (entry is Combine) 
				{
					Set subProjects;
					Set subReferences;
					
					CombineConfiguration cc = (entry as Combine).Configurations[config.Name] as CombineConfiguration;
					if ( cc == null ) continue;
					GetAllProjects ( cc, out subProjects, out subReferences);

					projects.Union (subProjects);
					references.Union (subReferences);
				}
			}
			
			references.Without (projects);
			combineProjects [config] = projects;
			combineReferences [config] = references;
		}
	}
}


