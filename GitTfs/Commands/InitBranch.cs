using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NDesk.Options;
using Sep.Git.Tfs.Core;
using StructureMap;
using Sep.Git.Tfs.Util;
using Sep.Git.Tfs.Core.TfsInterop;

namespace Sep.Git.Tfs.Commands
{
    [Pluggable("init-branch")]
    [Description("WARNING: This command is obsolete and will be removed in the next version. Use 'branch --init' instead!\n\ninit-branch [$/Repository/path <git-branch-name-wished>|--all]\n ex : git tfs init-branch $/Repository/ProjectBranch\n      git tfs init-branch $/Repository/ProjectBranch myNewBranch\n      git tfs init-branch --all\n      git tfs init-branch --tfs-parent-branch=$/Repository/ProjectParentBranch $/Repository/ProjectBranch")]
    [RequiresValidGitRepository]
    public class InitBranch : GitTfsCommand
    {
        private readonly TextWriter _stdout;
        private readonly Globals _globals;
        private readonly Help _helper;
        private readonly AuthorsFile _authors;

        private RemoteOptions _remoteOptions;
        public string TfsUsername { get; set; }
        public string TfsPassword { get; set; }
        public string IgnoreRegex { get; set; }
        public string ExceptRegex { get; set; }
        public string ParentBranch { get; set; }
        public bool CloneAllBranches { get; set; }
        public bool NoFetch { get; set; }
        public bool DontCreateGitBranch { get; set; }

        public IGitTfsRemote RemoteCreated { get; private set; }

        //[Temporary] Remove in the next version!
        public bool DontDisplayObsoleteMessage { get; set; }

        public InitBranch(TextWriter stdout, Globals globals, Help helper, AuthorsFile authors)
        {
            _stdout = stdout;
            _globals = globals;
            _helper = helper;
            _authors = authors;
        }

        public OptionSet OptionSet
        {
            get
            {
                return new OptionSet
                {
                    { "all", "Clone all the TFS branches (For TFS 2010 and later)", v => CloneAllBranches = (v.ToLower() == "all") },
                    { "b|tfs-parent-branch=", "TFS Parent branch of the TFS branch to clone (TFS 2008 only! And required!!) ex: $/Repository/ProjectParentBranch", v => ParentBranch = v },
                    { "u|username=", "TFS username", v => TfsUsername = v },
                    { "p|password=", "TFS password", v => TfsPassword = v },
                    { "ignore-regex=", "a regex of files to ignore", v => IgnoreRegex = v },
                    { "except-regex=", "a regex of exceptions to ignore-regex", v => ExceptRegex = v},
                    { "nofetch", "Create the new TFS remote but don't fetch any changesets", v => NoFetch = (v != null) }
                };
            }
        }

        public int Run(string tfsBranchPath)
        {
            return Run(tfsBranchPath, null);
        }

        public int Run(string tfsBranchPath, string gitBranchNameExpected)
        {
            //[Temporary] Remove in the next version!
            if (!DontDisplayObsoleteMessage)
                _stdout.WriteLine("WARNING: This command is obsolete and will be removed in the next version. Use 'branch --init' instead!");

            var defaultRemote = InitFromDefaultRemote();

            // TFS representations of repository paths do not have trailing slashes
            tfsBranchPath = (tfsBranchPath ?? string.Empty).TrimEnd('/');

            var allRemotes = _globals.Repository.ReadAllTfsRemotes();

            tfsBranchPath.AssertValidTfsPath();
            if (allRemotes.Any(r => r.TfsRepositoryPath.ToLower() == tfsBranchPath.ToLower()))
            {
                _stdout.WriteLine("warning : There is already a remote for this tfs branch. Branch ignored!");
                return GitTfsExitCodes.InvalidArguments;
            }

            IList<RootBranch> creationBranchData;
            if (ParentBranch == null)
                creationBranchData = defaultRemote.Tfs.GetRootChangesetForBranch(tfsBranchPath);
            else
            {
                var tfsRepositoryPathParentBranchFound = allRemotes.FirstOrDefault(r => r.TfsRepositoryPath.ToLower() == ParentBranch.ToLower());
                if (tfsRepositoryPathParentBranchFound == null)
                    throw new GitTfsException("error: The Tfs parent branch '" + ParentBranch +
                                              "' can not be found in the Git repository\nPlease init it first and try again...\n");

                creationBranchData = defaultRemote.Tfs.GetRootChangesetForBranch(tfsBranchPath, tfsRepositoryPathParentBranchFound.TfsRepositoryPath);
            }

            IFetchResult fetchResult;
            InitBranchSupportingRename(tfsBranchPath, gitBranchNameExpected, creationBranchData, defaultRemote, true, out fetchResult);
            return GitTfsExitCodes.OK;
        }

        private IGitTfsRemote InitBranchSupportingRename(string tfsBranchPath, string gitBranchNameExpected, IList<RootBranch> creationBranchData, IGitTfsRemote defaultRemote, bool failWithException, out IFetchResult fetchResult)
        {
            fetchResult = null;
            _stdout.WriteLine("Branches to Initialize successively :");
            foreach (var branch in creationBranchData)
            {
                _stdout.WriteLine("-" + branch.TfsBranchPath + " (" + branch.RootChangeset + ")");
            }

            IGitTfsRemote tfsRemote = null;
            var remoteToDelete = new List<IGitTfsRemote>();
            foreach (var rootBranch in creationBranchData)
            {
                Trace.WriteLine("Processing " + (rootBranch.IsRenamedBranch ? "renamed " : string.Empty) + "branch :" + rootBranch.TfsBranchPath + " (" +
                                rootBranch.RootChangeset + ")");
                var cbd = new BranchCreationDatas() {RootChangesetId = rootBranch.RootChangeset, TfsRepositoryPath = rootBranch.TfsBranchPath};
                if (cbd.TfsRepositoryPath == tfsBranchPath)
                    cbd.GitBranchNameExpected = gitBranchNameExpected;

                cbd.Sha1RootCommit = _globals.Repository.FindCommitHashByChangesetId(cbd.RootChangesetId);
                if (string.IsNullOrWhiteSpace(cbd.Sha1RootCommit))
                {
                    if (failWithException)
                        throw new GitTfsException("error: The root changeset " + cbd.RootChangesetId +
                                                  " have not be found in the Git repository. The branch containing the changeset should not have been created. Please do it before retrying!!\n");
                    return null;
                }

                Trace.WriteLine("Found commit " + cbd.Sha1RootCommit + " for changeset :" + cbd.RootChangesetId);

                tfsRemote = defaultRemote.InitBranch(_remoteOptions, cbd.TfsRepositoryPath, cbd.Sha1RootCommit, cbd.GitBranchNameExpected);
                RemoteCreated = tfsRemote;
                if (rootBranch.IsRenamedBranch || !NoFetch)
                {
                    fetchResult = FetchRemote(tfsRemote, false, !DontCreateGitBranch && !rootBranch.IsRenamedBranch);
                    if(fetchResult.IsSuccess && rootBranch.IsRenamedBranch)
                        remoteToDelete.Add(tfsRemote);
                    
                }
                else
                    Trace.WriteLine("Not fetching changesets, --nofetch option specified");
            }
            foreach (var gitTfsRemote in remoteToDelete)
            {
                _globals.Repository.DeleteTfsRemote(gitTfsRemote);
            }
            return tfsRemote;
        }


        class BranchCreationDatas
        {
            public string TfsRepositoryPath { get; set; }
            public string GitBranchNameExpected { get; set; }
            public long RootChangesetId { get; set; }
            public string Sha1RootCommit { get; set; }
        }

        class BranchDatas
        {
            public string TfsRepositoryPath { get; set; }
            public IGitTfsRemote TfsRemote { get; set; }
            public bool IsEntirelyFetched { get; set; }
            public long RootChangesetId { get; set; }
            public IList<RootBranch> CreationBranchData { get; set; }
            public Exception Error { get; set; }
        }

        public int Run()
        {
            //[Temporary] Remove in the next version!
            if (!DontDisplayObsoleteMessage)
                _stdout.WriteLine("WARNING: This command is obsolete and will be removed in the next version. Use 'branch --init' instead!");

            if (CloneAllBranches && NoFetch)
                throw new GitTfsException("error: --nofetch cannot be used with --all");

            if (!CloneAllBranches)
            {
                _helper.Run(this);
                return GitTfsExitCodes.Help;
            }

            var defaultRemote = InitFromDefaultRemote();

            var rootBranch = defaultRemote.Tfs.GetRootTfsBranchForRemotePath(defaultRemote.TfsRepositoryPath);
            if (rootBranch == null)
                throw new GitTfsException(string.Format("error: Init all the branches is only possible when 'git tfs clone' was done from the trunk!!! '{0}' is not a TFS branch!", defaultRemote.TfsRepositoryPath));
            if (defaultRemote.TfsRepositoryPath.ToLower() != rootBranch.Path.ToLower())
               throw new GitTfsException(string.Format("error: Init all the branches is only possible when 'git tfs clone' was done from the trunk!!! Please clone again from '{0}'...", rootBranch.Path));

            var childBranchPaths = rootBranch.GetAllChildren().Select(b => new BranchDatas {TfsRepositoryPath = b.Path}).ToList();

            if (childBranchPaths.Any())
            {
                _stdout.WriteLine("Tfs branches found:");
                var branchesToProcess = new List<BranchDatas>();
                foreach (var tfsBranchPath in childBranchPaths)
                {
                    _stdout.WriteLine("- " + tfsBranchPath.TfsRepositoryPath);
                    var branchDatas = new BranchDatas
                        {
                            TfsRepositoryPath = tfsBranchPath.TfsRepositoryPath,
                            TfsRemote = _globals.Repository.ReadAllTfsRemotes().FirstOrDefault(r => r.TfsRepositoryPath == tfsBranchPath.TfsRepositoryPath)
                        };
                    try
                    {
                        branchDatas.CreationBranchData = defaultRemote.Tfs.GetRootChangesetForBranch(tfsBranchPath.TfsRepositoryPath);
                    }
                    catch (Exception ex)
                    {
                        branchDatas.Error = ex;
                    }
                    
                    branchesToProcess.Add(branchDatas);
                }
                branchesToProcess.Add(new BranchDatas {TfsRepositoryPath = defaultRemote.TfsRepositoryPath, TfsRemote = defaultRemote, RootChangesetId = -1});

                bool isSomethingDone;
                do
                {
                    isSomethingDone = false;
                    var branchesToFetch = branchesToProcess.Where(b => !b.IsEntirelyFetched && b.Error == null).ToList();
                    foreach (var tfsBranch in branchesToFetch)
                    {
                        _stdout.WriteLine("=> Working on TFS branch : " + tfsBranch.TfsRepositoryPath);
                        if (tfsBranch.TfsRemote == null)
                        {
                            try
                            {
                                IFetchResult fetchResult;
                                tfsBranch.TfsRemote = InitBranchSupportingRename(tfsBranch.TfsRepositoryPath, null, tfsBranch.CreationBranchData, defaultRemote, false, out fetchResult);
                                if (tfsBranch.TfsRemote != null)
                                {
                                    tfsBranch.IsEntirelyFetched = fetchResult.IsSuccess;
                                    isSomethingDone = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                _stdout.WriteLine("error: an error occurs when initializing the branch. Branch is ignored and continuing...");
                                tfsBranch.Error = ex;
                            }
                        }
                        else
                        {
                            try
                            {
                                var lastFetchedChangesetId = tfsBranch.TfsRemote.MaxChangesetId;
                                Trace.WriteLine("Fetching remote :" + tfsBranch.TfsRemote.Id);
                                var fetchResult = FetchRemote(tfsBranch.TfsRemote, true);
                                tfsBranch.IsEntirelyFetched = fetchResult.IsSuccess;
                                if ( fetchResult.NewChangesetCount != 0)
                                    isSomethingDone = true;
                            }
                            catch (Exception ex)
                            {
                                _stdout.WriteLine("error: an error occurs when fetching changeset. Fetching is stopped and continuing...");
                                tfsBranch.Error = ex;
                            }
                        }
                    }
                } while (branchesToProcess.Any(b => !b.IsEntirelyFetched && b.Error == null) && isSomethingDone);

                if (branchesToProcess.Any(b => !b.IsEntirelyFetched))
                {
                    _stdout.WriteLine("warning: Some Tfs branches could not have been initialized:");
                    foreach (var branchNotInited in branchesToProcess.Where(b => !b.IsEntirelyFetched))
                    {
                        _stdout.WriteLine("- " + branchNotInited.TfsRepositoryPath);
                    }
                    _stdout.WriteLine("\nPlease report this case to the git-tfs developpers! (report here : https://github.com/git-tfs/git-tfs/issues/461 )");
                }
                if (branchesToProcess.Any(b => b.Error != null))
                {
                    _stdout.WriteLine("warning: Some Tfs branches could not have been initialized or entirely fetched due to errors:");
                    foreach (var branchWithErrors in branchesToProcess.Where(b => b.Error != null))
                    {
                        _stdout.WriteLine("- " + branchWithErrors.TfsRepositoryPath);
                        if(_globals.DebugOutput)
                            Trace.WriteLine("   =>error:" + branchWithErrors.Error);
                        else
                            _stdout.WriteLine("   =>error:" + branchWithErrors.Error.Message);
                    }
                    _stdout.WriteLine("\nPlease report this case to the git-tfs developpers! (report here : https://github.com/git-tfs/git-tfs/issues )");
                }
            }
            else
            {
                _stdout.WriteLine("No other Tfs branches found.");
            }
            return GitTfsExitCodes.OK;
        }


        private IGitTfsRemote InitFromDefaultRemote()
        {
            var defaultRemote = _globals.Repository.ReadTfsRemote(GitTfsConstants.DefaultRepositoryId);
            if (defaultRemote == null)
                throw new GitTfsException("error: No git-tfs repository found. Please try to clone first...\n");

            _remoteOptions = new RemoteOptions();
            if (!string.IsNullOrWhiteSpace(TfsUsername))
            {
                _remoteOptions.Username = TfsUsername;
                _remoteOptions.Password = TfsPassword;
            }
            else
            {
                _remoteOptions.Username = defaultRemote.TfsUsername;
                _remoteOptions.Password = defaultRemote.TfsPassword;
            }

            if (IgnoreRegex != null)
                _remoteOptions.IgnoreRegex = IgnoreRegex;
            else
                _remoteOptions.IgnoreRegex = defaultRemote.IgnoreRegexExpression;

            if (ExceptRegex != null)
                _remoteOptions.ExceptRegex = ExceptRegex;
            else
                _remoteOptions.ExceptRegex = defaultRemote.IgnoreExceptRegexExpression;

            return defaultRemote;
        }


        private IFetchResult FetchRemote(IGitTfsRemote tfsRemote, bool stopOnFailMergeCommit, bool createBranch = true)
        {
            Trace.WriteLine("Try fetching changesets...");
            var fetchResult = tfsRemote.Fetch(stopOnFailMergeCommit);
            Trace.WriteLine("Changesets fetched!");

            if (fetchResult.IsSuccess && createBranch && tfsRemote.Id != GitTfsConstants.DefaultRepositoryId)
            {
                Trace.WriteLine("Try creating the local branch...");
                if (!_globals.Repository.CreateBranch("refs/heads/" + tfsRemote.Id, tfsRemote.MaxCommitHash))
                    _stdout.WriteLine("warning: Fail to create local branch ref file!");
                else
                    Trace.WriteLine("Local branch created!");
            }

            Trace.WriteLine("Cleaning...");
            tfsRemote.CleanupWorkspaceDirectory();

            return fetchResult;
        }
    }
}
