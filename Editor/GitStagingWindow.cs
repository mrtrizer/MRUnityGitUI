using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using static UnityEngine.ParticleSystem;

namespace Abuksigun.PackageShortcuts
{
    public static class GitStaging
    {
        [MenuItem("Assets/Git Staging", true)]
        public static bool Check() => PackageShortcuts.GetSelectedGitModules().Any();
        [MenuItem("Window/Git GUI/Staging")]
        [MenuItem("Assets/Git Staging", priority = 100)]
        public static async void Invoke()
        {
            if (EditorWindow.GetWindow<GitStagingWindow>() is { } window && window)
            {
                window.titleContent = new GUIContent("Git Staging");
                window.Show();
            }
        }
    }
    
    public class GitStagingWindow : DefaultWindow
    {
        const int TopPanelHeight = 130;
        const int MiddlePanelWidth = 40;

        record Selection(ListState Unstaged, ListState Staged);

        TreeViewState treeViewStateUnstaged = new();
        LazyTreeView<GitStatus> treeViewUnstaged;
        TreeViewState treeViewStateStaged = new();
        LazyTreeView<GitStatus> treeViewStaged;
        string commitMessage = "";
        string guid = null;
        List<Task<CommandResult>> tasksInProgress = new ();
        Dictionary<Module, Selection> selectionPerModule = new ();

        protected override void OnGUI()
        {
            treeViewUnstaged ??= new(statuses => GUIShortcuts.GenerateFileItems(statuses, false), treeViewStateUnstaged, true);
            treeViewStaged ??= new(statuses => GUIShortcuts.GenerateFileItems(statuses, true), treeViewStateStaged, true);

            var modules = PackageShortcuts.GetSelectedGitModules().ToList();

            GUILayout.Label("Commit message");
            commitMessage = GUILayout.TextArea(commitMessage, GUILayout.Height(40));

            tasksInProgress.RemoveAll(x => x.IsCompleted);

            var modulesInMergingState = modules.Where(x => x.IsMergeInProgress.GetResultOrDefault());
            var moduleNotInMergeState = modules.Where(x => !x.IsMergeInProgress.GetResultOrDefault());
            int modulesWithStagedFiles = moduleNotInMergeState.Count(x => x.GitStatus.GetResultOrDefault()?.Staged?.Count() > 0);
            bool commitAvailable = modulesWithStagedFiles > 0 && !string.IsNullOrWhiteSpace(commitMessage) && !tasksInProgress.Any();

            using (new GUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledGroupScope(!commitAvailable))
                {
                    if (GUILayout.Button($"Commit {modulesWithStagedFiles}/{modules.Count} modules", GUILayout.Width(200)))
                    {
                        tasksInProgress.AddRange(moduleNotInMergeState.Select(module => module.RunGit($"commit -m {commitMessage.WrapUp()}")));
                        commitMessage = "";
                    }
                    if (GUILayout.Button($"Stash {modulesWithStagedFiles}/{modules.Count} modules", GUILayout.Width(200)))
                    {
                        tasksInProgress.AddRange(moduleNotInMergeState.Select(module => {
                            var files = module.GitStatus.GetResultOrDefault().Files.Where(x => x.IsStaged).Select(x => x.FullPath);
                            return module.RunGit($"stash push -m {commitMessage.WrapUp()} -- {PackageShortcuts.JoinFileNames(files)}");
                        }));
                        commitMessage = "";
                    }
                }
                if (modulesInMergingState.Any() && GUILayout.Button($"Commit merge in {modulesInMergingState.Count()}/{modules.Count} modules", GUILayout.Width(200))
                    && EditorUtility.DisplayDialog($"Are you sure you want COMMIT merge?", "It will be default commit message for each module. You can't change it!", "Yes", "No"))
                {
                    tasksInProgress.AddRange(modules.Select(module => module.RunGit($"commit --no-edit")));
                }
                if (modulesInMergingState.Any() && GUILayout.Button($"Abort merge in {modulesInMergingState.Count()}/{modules.Count} modules", GUILayout.Width(200))
                    && EditorUtility.DisplayDialog($"Are you sure you want ABORT merge?", modulesInMergingState.Select(x => x.Name).Join(", "), "Yes", "No"))
                {
                    tasksInProgress.AddRange(modules.Select(module => module.RunGit($"merge --abort")));
                }
            }
            GUILayout.Space(20);

            var statuses = modules.Select(x => x.GitStatus.GetResultOrDefault()).Where(x => x != null);
            var unstagedSelection = statuses.SelectMany(x => x.Files).Where(x => treeViewUnstaged.HasFocus() && treeViewStateUnstaged.selectedIDs.Contains(x.FullPath.GetHashCode()));
            var stagedSelection = statuses.SelectMany(x => x.Files).Where(x => treeViewStaged.HasFocus() && treeViewStateStaged.selectedIDs.Contains(x.FullPath.GetHashCode()));
            if (unstagedSelection.Any())
                PackageShortcuts.SetSelectedFiles(unstagedSelection, false);
            if (stagedSelection.Any())
                PackageShortcuts.SetSelectedFiles(stagedSelection, true);

            using (new EditorGUI.DisabledGroupScope(tasksInProgress.Any()))
            using (new GUILayout.HorizontalScope())
            {
                var size = new Vector2((position.width - MiddlePanelWidth) / 2, position.height - TopPanelHeight);
                
                treeViewUnstaged.Draw(size, statuses, (int id) => ShowContextMenu(modules, unstagedSelection));
                using (new GUILayout.VerticalScope())
                {
                    GUILayout.Space(50);
                    if (GUILayout.Button(EditorGUIUtility.IconContent("tab_next@2x"), GUILayout.Width(MiddlePanelWidth)))
                    {
                        GUIShortcuts.Stage(modules.Select(module => (module, PackageShortcuts.JoinFileNames(unstagedSelection.Where(x => x.ModuleGuid == module.Guid)))));
                        treeViewStateUnstaged.selectedIDs.Clear();
                    }
                    if (GUILayout.Button(EditorGUIUtility.IconContent("tab_prev@2x"), GUILayout.Width(MiddlePanelWidth)))
                    {
                        GUIShortcuts.Unstage(modules.Select(module => (module, PackageShortcuts.JoinFileNames(stagedSelection.Where(x => x.ModuleGuid == module.Guid)))));
                        treeViewStateStaged.selectedIDs.Clear();
                    }
                }
                treeViewStaged.Draw(size, statuses, (int id) => ShowContextMenu(modules, stagedSelection));
            }

            base.OnGUI();
        }

        static void ShowContextMenu(IEnumerable<Module> modules, IEnumerable<FileStatus> files)
        {
            if (!files.Any())
                return;
            var menu = new GenericMenu();
            var selectionPerModule = modules.Select(module => (module, files:PackageShortcuts.JoinFileNames(files.Where(x => x.ModuleGuid == module.Guid))));
            if (files.Any(x => x.IsInIndex))
            {
                menu.AddItem(new GUIContent("Diff"), false, () => {
                    foreach (var module in modules)
                        GitDiff.ShowDiff();
                });
                menu.AddItem(new GUIContent("Log"), false, async () => {
                    foreach ((var module, var files) in selectionPerModule)
                    {
                        var window = ScriptableObject.CreateInstance<GitLogWindow>();
                        window.titleContent = new GUIContent("Log Files");
                        window.LogFiles = files;
                        await GUIShortcuts.ShowModalWindow(window, new Vector2Int(800, 700));
                    }
                });
                menu.AddSeparator("");
                string message = selectionPerModule.Select(x => x.files).Join('\n');
                if (files.Any(x => x.IsUnstaged))
                {
                    menu.AddItem(new GUIContent("Discrad"), false, () => GUIShortcuts.DiscardFiles(selectionPerModule));
                    menu.AddItem(new GUIContent("Stage"), false, () => GUIShortcuts.Stage(selectionPerModule));
                }
                if (files.Any(x => x.IsStaged))
                    menu.AddItem(new GUIContent("Unstage"), false, () => GUIShortcuts.Unstage(selectionPerModule));
            }

            if (files.Any(x => x.IsUnresolved))
            {
                Dictionary<Module, string> conflictedFilesList = modules.ToDictionary(
                module => module,
                module => PackageShortcuts.JoinFileNames(files.Where(x => x.IsUnresolved && x.ModuleGuid == module.Guid).Select(x => x.FullPath)));
                string message = conflictedFilesList.Select(x => x.Value).Join('\n');
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Take Ours"), false, () => {
                    if (EditorUtility.DisplayDialog($"Do you want to take OURS changes (git checkout --ours --)", message, "Yes", "No"))
                    {
                        foreach (var module in modules)
                            module.RunGit($"checkout --ours  -- {conflictedFilesList[module]}");
                    }
                });
                menu.AddItem(new GUIContent("Take Theirs"), false, () => {
                    if (EditorUtility.DisplayDialog($"Do you want to take THEIRS changes (git checkout --theirs --)", message, "Yes", "No"))
                    {
                        foreach (var module in modules)
                            module.RunGit($"checkout --theirs  -- {conflictedFilesList[module]}");
                    }
                });
            }
            menu.AddItem(new GUIContent("Delete"), false, () => {
                if (EditorUtility.DisplayDialog($"Are you sure you want DELETE these files", selectionPerModule.Select(x => x.files).Join('\n'), "Yes", "No"))
                {
                    foreach (var file in files)
                        File.Delete(file.FullPath);
                }
            });
            menu.ShowAsContext();
        }
    }
}