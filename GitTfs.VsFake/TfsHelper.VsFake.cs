using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sep.Git.Tfs.Commands;
using Sep.Git.Tfs.Core;
using Sep.Git.Tfs.Core.TfsInterop;
using Sep.Git.Tfs.Util;
using StructureMap;

namespace Sep.Git.Tfs.VsFake
{
    public class MockBranchObject : IBranchObject
    {
        public string Path { get; set; }

        public string ParentPath { get; set; }

        public bool IsRoot { get; set; }
    }

    public class TfsHelper : ITfsHelper
    {
        #region misc/null

        IContainer _container;
        TextWriter _stdout;
        Script _script;

        public TfsHelper(IContainer container, TextWriter stdout, Script script)
        {
            _container = container;
            _stdout = stdout;
            _script = script;
        }

        public string TfsClientLibraryVersion { get { return "(FAKE)"; } }

        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public void EnsureAuthenticated() {}

        public bool CanShowCheckinDialog { get { return false; } }

        public long ShowCheckinDialog(IWorkspace workspace, IPendingChange[] pendingChanges, IEnumerable<IWorkItemCheckedInfo> checkedInfos, string checkinComment)
        {
            throw new NotImplementedException();
        }

        public IIdentity GetIdentity(string username)
        {
            return new NullIdentity();
        }

        #endregion

        #region read changesets

        public ITfsChangeset GetLatestChangeset(IGitTfsRemote remote)
        {
            return _script.Changesets.LastOrDefault().AndAnd(x => BuildTfsChangeset(x, remote));
        }

        public IEnumerable<ITfsChangeset> GetChangesets(CancellationToken token, string path, long startVersion, IGitTfsRemote remote)
        {
            if (!_script.Changesets.Any(c => c.IsBranchChangeset) && _script.Changesets.Any(c => c.IsMergeChangeset))
                return _script.Changesets.Where(x => x.Id >= startVersion).Select(x => BuildTfsChangeset(x, remote));
            return _script.Changesets
                .Where(x => x.Id >= startVersion && x.Changes.Any(c => c.RepositoryPath.ToLower().IndexOf(path.ToLower()) == 0 || path.ToLower().IndexOf(c.RepositoryPath.ToLower()) == 0))
                .Select(x => BuildTfsChangeset(x, remote));
        }

        public int FindMergeChangesetParent(string path, long firstChangeset, GitTfsRemote remote)
        {
            var firstChangesetOfBranch = _script.Changesets.FirstOrDefault(c => c.IsMergeChangeset && c.MergeChangesetDatas.MergeIntoBranch == path && c.MergeChangesetDatas.BeforeMergeChangesetId < firstChangeset);
            if (firstChangesetOfBranch != null)
                return firstChangesetOfBranch.MergeChangesetDatas.BeforeMergeChangesetId;
            return -1;

        }

        private ITfsChangeset BuildTfsChangeset(ScriptedChangeset changeset, IGitTfsRemote remote)
        {
            var tfsChangeset = _container.With<ITfsHelper>(this).With<IChangeset>(new Changeset(changeset)).GetInstance<TfsChangeset>();
            tfsChangeset.Summary = new TfsChangesetInfo { ChangesetId = changeset.Id, Remote = remote };
            return tfsChangeset;
        }

        class Changeset : IChangeset
        {
            private ScriptedChangeset _changeset;

            public Changeset(ScriptedChangeset changeset)
            {
                _changeset = changeset;
            }

            public IChange[] Changes
            {
                get { return _changeset.Changes.Select(x => new Change(_changeset, x)).ToArray(); }
            }

            public string Committer
            {
                get { return "todo"; }
            }

            public DateTime CreationDate
            {
                get { return _changeset.CheckinDate; }
            }

            public string Comment
            {
                get { return _changeset.Comment; }
            }

            public int ChangesetId
            {
                get { return _changeset.Id; }
            }

            public IVersionControlServer VersionControlServer
            {
                get { throw new NotImplementedException(); }
            }

            public void Get(IWorkspace workspace)
            {
                workspace.GetSpecificVersion(this);
            }
        }

        class Change : IChange, IItem
        {
            ScriptedChangeset _changeset;
            ScriptedChange _change;

            public Change(ScriptedChangeset changeset, ScriptedChange change)
            {
                _changeset = changeset;
                _change = change;
            }

            TfsChangeType IChange.ChangeType
            {
                get { return _change.ChangeType; }
            }

            IItem IChange.Item
            {
                get { return this; }
            }

            IVersionControlServer IItem.VersionControlServer
            {
                get { throw new NotImplementedException(); }
            }

            int IItem.ChangesetId
            {
                get { return _changeset.Id; }
            }

            string IItem.ServerItem
            {
                get { return _change.RepositoryPath; }
            }

            int IItem.DeletionId
            {
                get { return 0; }
            }

            TfsItemType IItem.ItemType
            {
                get { return _change.ItemType; }
            }

            int IItem.ItemId
            {
                get { throw new NotImplementedException(); }
            }

            long IItem.ContentLength
            {
                get
                {
                    using (var temp = ((IItem)this).DownloadFile())
                        return new FileInfo(temp).Length;
                }
            }

            TemporaryFile IItem.DownloadFile()
            {
                var temp = new TemporaryFile();
                using(var writer = new StreamWriter(temp))
                    writer.Write(_change.Content);
                return temp;
            }
        }

        #endregion

        #region workspaces

         public void WithWorkspace(string localDirectory, IGitTfsRemote remote, IEnumerable<Tuple<string, string>> mappings, TfsChangesetInfo versionToFetch, Action<ITfsWorkspace> action)
         {
             Trace.WriteLine("Setting up a TFS workspace at " + localDirectory);
             var fakeWorkspace = new FakeWorkspace(localDirectory, remote.TfsRepositoryPath);
             var workspace = _container.With("localDirectory").EqualTo(localDirectory)
                 .With("remote").EqualTo(remote)
                 .With("contextVersion").EqualTo(versionToFetch)
                 .With("workspace").EqualTo(fakeWorkspace)
                 .With("tfsHelper").EqualTo(this)
                 .GetInstance<TfsWorkspace>();
             action(workspace);
         }

        public void WithWorkspace(string directory, IGitTfsRemote remote, TfsChangesetInfo versionToFetch, Action<ITfsWorkspace> action)
        {
            Trace.WriteLine("Setting up a TFS workspace at " + directory);
            var fakeWorkspace = new FakeWorkspace(directory, remote.TfsRepositoryPath);
            var workspace = _container.With("localDirectory").EqualTo(directory)
                .With("remote").EqualTo(remote)
                .With("contextVersion").EqualTo(versionToFetch)
                .With("workspace").EqualTo(fakeWorkspace)
                .With("tfsHelper").EqualTo(this)
                .GetInstance<TfsWorkspace>();
            action(workspace);
        }

        class FakeWorkspace : IWorkspace
        {
            string _directory;
            string _repositoryRoot;

            public FakeWorkspace(string directory, string repositoryRoot)
            {
                _directory = directory;
                _repositoryRoot = repositoryRoot;
            }

            public void GetSpecificVersion(IChangeset changeset)
            {
                var repositoryRoot = _repositoryRoot.ToLower();
                if(!repositoryRoot.EndsWith("/")) repositoryRoot += "/";
                foreach (var change in changeset.Changes)
                {
                    if (change.Item.ItemType == TfsItemType.File)
                    {
                        var outPath = Path.Combine(_directory, change.Item.ServerItem.ToLower().Replace(repositoryRoot, ""));
                        var outDir = Path.GetDirectoryName(outPath);
                        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                        using (var download = change.Item.DownloadFile())
                            File.WriteAllText(outPath, File.ReadAllText(download.Path));
                    }
                }
            }

            #region unimplemented

            public void Merge(string sourceTfsPath, string tfsRepositoryPath)
            {
                throw new NotImplementedException();
            }

            public IPendingChange[] GetPendingChanges()
            {
                throw new NotImplementedException();
            }

            public ICheckinEvaluationResult EvaluateCheckin(TfsCheckinEvaluationOptions options, IPendingChange[] allChanges, IPendingChange[] changes, string comment, ICheckinNote checkinNote, IEnumerable<IWorkItemCheckinInfo> workItemChanges)
            {
                throw new NotImplementedException();
            }

            public ICheckinEvaluationResult EvaluateCheckin(TfsCheckinEvaluationOptions options, IPendingChange[] allChanges, IPendingChange[] changes, string comment, string authors, ICheckinNote checkinNote, IEnumerable<IWorkItemCheckinInfo> workItemChanges)
            {
                throw new NotImplementedException();
            }

            public void Shelve(IShelveset shelveset, IPendingChange[] changes, TfsShelvingOptions options)
            {
                throw new NotImplementedException();
            }

            public int Checkin(IPendingChange[] changes, string comment, string author, ICheckinNote checkinNote, IEnumerable<IWorkItemCheckinInfo> workItemChanges, TfsPolicyOverrideInfo policyOverrideInfo, bool overrideGatedCheckIn)
            {
                throw new NotImplementedException();
            }

            public int PendAdd(string path)
            {
                throw new NotImplementedException();
            }

            public int PendEdit(string path)
            {
                throw new NotImplementedException();
            }

            public int PendDelete(string path)
            {
                throw new NotImplementedException();
            }

            public int PendRename(string pathFrom, string pathTo)
            {
                throw new NotImplementedException();
            }

            public void ForceGetFile(string path, int changeset)
            {
                throw new NotImplementedException();
            }

            public void GetSpecificVersion(int changeset)
            {
                throw new NotImplementedException();
            }

            public string GetLocalItemForServerItem(string serverItem)
            {
                throw new NotImplementedException();
            }

            public string GetServerItemForLocalItem(string localItem)
            {
                throw new NotImplementedException();
            }

            public string OwnerName
            {
                get { throw new NotImplementedException(); }
            }

            #endregion
        }

        public void CleanupWorkspaces(string workingDirectory)
        {
        }

        public bool IsExistingInTfs(string path)
        {
            var exists = false;
            foreach (var changeset in _script.Changesets)
            {
                foreach (var change in changeset.Changes)
                {
                    if (change.RepositoryPath == path)
                    {
                        exists = !change.ChangeType.IncludesOneOf(TfsChangeType.Delete);
                    }
                }
            }
            return exists;
        }

        public bool CanGetBranchInformation { get { return false; } }

        public IChangeset GetChangeset(int changesetId)
        {
            return new Changeset(_script.Changesets.First(c => c.Id == changesetId));
        }

        public int GetRootChangesetForBranch(CancellationToken token, string tfsPathBranchToCreate, string tfsPathParentBranch = null)
        {
            var firstChangesetOfBranch = _script.Changesets.FirstOrDefault(c => c.IsBranchChangeset && c.BranchChangesetDatas.BranchPath == tfsPathBranchToCreate);
            if (firstChangesetOfBranch != null)
                return firstChangesetOfBranch.BranchChangesetDatas.RootChangesetId;
            return -1;
        }

        public IEnumerable<IBranchObject> GetBranches()
        {
            var branches = new List<IBranchObject>();
            branches.AddRange(_script.RootBranches.Select(b => new MockBranchObject { IsRoot = true, Path = b.BranchPath, ParentPath = null }));
            branches.AddRange(_script.Changesets.Where(c=>c.IsBranchChangeset).Select(c => new MockBranchObject
                    {
                        IsRoot = false,
                        Path = c.BranchChangesetDatas.BranchPath,
                        ParentPath = c.BranchChangesetDatas.ParentBranch
                    }));
            return branches;
        }
        #endregion

        #region unimplemented

        public IShelveset CreateShelveset(IWorkspace workspace, string shelvesetName)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<IWorkItemCheckinInfo> GetWorkItemInfos(IEnumerable<string> workItems, TfsWorkItemCheckinAction checkinAction)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<IWorkItemCheckedInfo> GetWorkItemCheckedInfos(IEnumerable<string> workItems, TfsWorkItemCheckinAction checkinAction)
        {
            throw new NotImplementedException();
        }

        public ICheckinNote CreateCheckinNote(Dictionary<string, string> checkinNotes)
        {
            throw new NotImplementedException();
        }

        public ITfsChangeset GetChangeset(int changesetId, IGitTfsRemote remote)
        {
            throw new NotImplementedException();
        }
        public bool HasShelveset(string shelvesetName)
        {
            throw new NotImplementedException();
        }

        public ITfsChangeset GetShelvesetData(IGitTfsRemote remote, string shelvesetOwner, string shelvesetName)
        {
            throw new NotImplementedException();
        }

        public int ListShelvesets(ShelveList shelveList, IGitTfsRemote remote)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> GetAllTfsRootBranchesOrderedByCreation()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<TfsLabel> GetLabels(string tfsPathBranch, string nameFilter = null)
        {
            throw new NotImplementedException();
        }

        public void CreateBranch(string sourcePath, string targetPath, int changesetId, string comment = null)
        {
            throw new NotImplementedException();
        }

        public void CreateTfsRootBranch(string projectName, string mainBranch, string gitRepositoryPath, bool createTeamProjectFolder)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
