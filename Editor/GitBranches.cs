using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Abuksigun.PackageShortcuts
{
    class SimpleTreeView : TreeView
    {
        Action<int> contextMenuCallback;
        IList<TreeViewItem> items;
        bool multiSelection;

        public SimpleTreeView(TreeViewState treeViewState, bool multiSelection) : base(treeViewState)
        {
            this.multiSelection = multiSelection;
        }

        public void Draw(Vector2 size, IList<TreeViewItem> items, Action<int> contextMenuCallback = null)
        {
            this.items = items;
            this.contextMenuCallback = contextMenuCallback;
            Reload();
            OnGUI(GUILayoutUtility.GetRect(size.x, size.y));
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            SetupParentsAndChildrenFromDepths(root, items);
            return root;
        }

        protected override bool CanMultiSelect(TreeViewItem item)
        {
            return multiSelection;
        }

        protected override void ContextClickedItem(int id)
        {
            contextMenuCallback?.Invoke(id);
            base.ContextClickedItem(id);
        }
    }


    public class RefComparer : EqualityComparer<Branch>
    {
        public override bool Equals(Branch x, Branch y)
        {
            return (x is LocalBranch localBranchX && y is LocalBranch localBranchY && localBranchX.Name == localBranchY.Name)
                || (x is RemoteBranch remoteBranchX && y is RemoteBranch remoteBranchY && remoteBranchX.QualifiedName == remoteBranchY.QualifiedName);
        }

        public override int GetHashCode(Branch obj)
        {
            return obj.QualifiedName.GetHashCode();
        }
    }
    
    class GitBranchesWindow : DefaultWindow
    {
        static RefComparer refComparer = new();

        const int BottomPanelHeight = 25;

        bool showAllBranches = false;
        Task checkoutTask = null;

        SimpleTreeView simpleTreeView;
        [SerializeField]
        TreeViewState treeViewState;

        protected override void OnGUI()
        {
            var modules = PackageShortcuts.GetSelectedGitModules();
            var branchesPerRepo = modules.Select(module => module.Branches.GetResultOrDefault());
            var currentBranchPerRepo = modules.ToDictionary(module => module, module => module.CurrentBranch.GetResultOrDefault());
            if (!branchesPerRepo.Any() || branchesPerRepo.Any(x => x == null))
                return;

            Branch[] branches = branchesPerRepo.Count() == 1 ? branchesPerRepo.First()
                : showAllBranches ? branchesPerRepo.SelectMany(x => x).Distinct(refComparer).ToArray()
                : branchesPerRepo.Skip(1).Aggregate(branchesPerRepo.First().AsEnumerable(), (result, nextArray) => result.Intersect(nextArray, refComparer)).ToArray();

            simpleTreeView ??= new SimpleTreeView(treeViewState ??= new TreeViewState(), false);
            var items = new List<TreeViewItem>();
            items.Add(new TreeViewItem(0, 0, "Branches"));
            BranchesToItems(modules, branches, x => x is LocalBranch, 1, items);
            items.Add(new TreeViewItem(1, 0, "Remotes"));
            BranchesToItems(modules, branches, x => x is RemoteBranch, 1, items);
            items.Add(new TreeViewItem(2, 0, "Tags"));
            BranchesToItems(modules, branches, x => x is Tag, 1, items);
            items.Add(new TreeViewItem(3, 0, "Stashes"));
            BranchesToItems(modules, branches, x => x is Stash, 1, items);
            simpleTreeView.Draw(new Vector2(position.width, position.height - BottomPanelHeight), items, id => {
                if (checkoutTask == null || checkoutTask.IsCompleted)
                    ContextMenu(modules, branches.FirstOrDefault(x => x.QualifiedName.GetHashCode() == id));
            });

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New Branch", GUILayout.Width(150)))
                    MakeBranch();

                showAllBranches = GUILayout.Toggle(showAllBranches, "Show All Branches");
            }
            base.OnGUI();
        }
        
        static async void MakeBranch()
        {
            string branchName = "";
            bool checkout = true;
            Task task = null;

            await GUIShortcuts.ShowModalWindow("Make branch", new Vector2Int(300, 150), (window) => {
                GUILayout.Label("Branch Name: ");
                branchName = EditorGUILayout.TextField(branchName);
                checkout = GUILayout.Toggle(checkout, "Checkout to this branch");
                GUILayout.Space(40);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Ok", GUILayout.Width(200)))
                    {
                        task = Task.WhenAll(PackageShortcuts.GetSelectedGitModules().Select(module => module.RunGit(checkout ? $"checkout -b {branchName}" : $"branch {branchName}")));
                        window.Close();
                    }
                }
            });

            if (task != null)
                await task;
        }

        void ContextMenu(IEnumerable<Module> modules, Branch selectedBranch)
        {
            string branchName = selectedBranch.QualifiedName.Replace("/", "\u2215");
            GenericMenu menu = new GenericMenu();
            if (selectedBranch is LocalBranch localBranch)
            {
                menu.AddItem(new GUIContent($"Checkout [{branchName}]"), false, () => {
                    checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"checkout {localBranch.Name}")));
                });

                menu.AddItem(new GUIContent($"Delete local [{branchName}]"), false, () => {
                    if (EditorUtility.DisplayDialog("Are you sure you want DELETE branch", $"LOCAL {localBranch.Name} in {modules.Count()} modules", "Yes", "No"))
                        checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"branch -d {localBranch.Name}")));
                });
            }
            else if (selectedBranch is RemoteBranch remoteBranch)
            {
                
                menu.AddItem(new GUIContent($"Checkout & Track [{branchName}]"), false, () => {
                    checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"switch {remoteBranch.Name}")));
                });

                menu.AddItem(new GUIContent($"Delete remote [{branchName}]"), false, () => {
                    if (EditorUtility.DisplayDialog("Are you sure you want DELETE branch", $"REMOTE {remoteBranch.Name} in {modules.Count()} modules", "Yes", "No"))
                        checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"push -d {remoteBranch.RemoteAlias} {remoteBranch.Name}")));
                });
            }

            string affectedModules = modules.Select(x => $"{x.Name}: {selectedBranch.Name} into {x.CurrentBranch.GetResultOrDefault()}").Join('\n');

            menu.AddItem(new GUIContent($"Merge [{branchName}]"), false, () => {
                if (EditorUtility.DisplayDialog("Are you sure you want MERGE branch", affectedModules, "Yes", "No"))
                    checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"merge {selectedBranch.QualifiedName}")));
            });

            menu.AddItem(new GUIContent($"Rebase [{branchName}]"), false, () => {
                if (EditorUtility.DisplayDialog("Are you sure you want REBASE branch", affectedModules, "Yes", "No"))
                    checkoutTask = Task.WhenAll(modules.Select(module => GUIShortcuts.RunGitAndErrorCheck(module, $"rebase {selectedBranch.QualifiedName}")));
            });
            menu.ShowAsContext();
        }

        List<TreeViewItem> BranchesToItems(IEnumerable<Module> modules, Reference[] branches, Func<Reference, bool> filter, int rootDepth, List<TreeViewItem> items)
        {
            var branchesPerRepo = modules.Select(module => module.Branches.GetResultOrDefault());
            var currentBranchPerRepo = modules.ToDictionary(module => module, module => module.CurrentBranch.GetResultOrDefault());
            string currentPath = "";
            foreach (var branch in branches.Where(filter).OrderBy(x => x.QualifiedName))
            {
                int lastSlashIndex = branch.QualifiedName.LastIndexOf('/');
                if (lastSlashIndex != -1 && currentPath != branch.QualifiedName.Substring(0, lastSlashIndex))
                {
                    currentPath = branch.QualifiedName.Substring(0, lastSlashIndex);
                    var parts = currentPath.Split('/');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        int hashCode = parts[0..(i + 1)].Join('/').GetHashCode();
                        if (!items.Any(x => x.id == hashCode))
                            items.Add(new TreeViewItem(hashCode, rootDepth + i, parts[i]));
                    }
                }
                int depth = branch.QualifiedName.Count(x => x == '/');
                string reposOnBranch = currentBranchPerRepo.Where(x => x.Value == branch.QualifiedName).Select(x => x.Key.Name).Join(',');
                int reposHaveBranch = branchesPerRepo.Count(x => x.Any(y => y.QualifiedName == branch.QualifiedName));
                string reposHaveBranchStr = reposHaveBranch.ToString().WrapUp("(", ")");
                string itemText = 
                    $"{branch.QualifiedName.Substring(lastSlashIndex + 1)} " +
                    $"{reposHaveBranchStr.When(reposHaveBranch != modules.Count())} " +
                    $"{reposOnBranch.WrapUp("[", "]").When(reposOnBranch != "")}";
                var item = new TreeViewItem(branch.QualifiedName.GetHashCode(), rootDepth + depth, itemText);
                items.Add(item);
            }
            return items;
        }
    }

    public static class GitBranches
    {
        [MenuItem("Assets/Git Branches", true)]
        public static bool Check() => PackageShortcuts.GetSelectedGitModules().Any();

        [MenuItem("Assets/Git Branches", priority = 100)]
        public static async void Invoke()
        {
            var window = ScriptableObject.CreateInstance<GitBranchesWindow>();
            window.titleContent = new GUIContent("Branches Manager");
            window.Show();
        }
    }
}