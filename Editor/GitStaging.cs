using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Abuksigun.PackageShortcuts
{
    public static class GitStaging
    {
        const int TopPanelHeight = 150;
        const int MiddlePanelWidth = 40;
        const int BottomPanelHeight = 17;

        [MenuItem("Assets/Git Staging", true)]
        public static bool Check() => PackageShortcuts.GetSelectedGitModules().Any();

        [MenuItem("Assets/Git Staging", priority = 100)]
        public static async void Invoke()
        {
            string commitMessage = "";
            var modules = PackageShortcuts.GetSelectedGitModules().ToArray();
            var tasksInProgress = new List<Task<CommandResult>>();
            var selection = Enumerable.Repeat((unstaged:new ListState(), staged: new ListState()), modules.Length).ToArray();

            bool showHidden = false;
            string[] moduleNames = modules.Select(x => x.ShortName).ToArray();
            int tab = 0;
            
            await GUIShortcuts.ShowModalWindow("Commit", new Vector2Int(800, 500), (window) => {
                
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
                        if (GUILayout.Button($"Commit {modulesWithStagedFiles}/{modules.Length} modules", GUILayout.Width(200)))
                        {
                            tasksInProgress.AddRange(moduleNotInMergeState.Select(module => module.RunGit($"commit -m {commitMessage.WrapUp()}")));
                            commitMessage = "";
                        }
                        if (GUILayout.Button($"Stash {modulesWithStagedFiles}/{modules.Length} modules", GUILayout.Width(200)))
                        {
                            tasksInProgress.AddRange(moduleNotInMergeState.Select(module => {
                                var files = module.GitStatus.GetResultOrDefault().Files.Where(x => x.IsStaged).Select(x => x.FullPath);
                                return module.RunGit($"stash push -m {commitMessage.WrapUp()} -- {PackageShortcuts.JoinFileNames(files)}");
                            }));
                            commitMessage = "";
                        } 
                    }
                    if (modulesInMergingState.Any() && GUILayout.Button($"Commit merge in {modulesInMergingState.Count()}/{modules.Length} modules", GUILayout.Width(200))
                        && EditorUtility.DisplayDialog($"Are you sure you want COMMIT merge?", "It will be default commit message for each module. You can't change it!", "Yes", "No"))
                    {
                        tasksInProgress.AddRange(modules.Select(module => module.RunGit($"commit --no-edit")));
                    }
                    if (modulesInMergingState.Any() && GUILayout.Button($"Abort merge in {modulesInMergingState.Count()}/{modules.Length} modules", GUILayout.Width(200))
                        && EditorUtility.DisplayDialog($"Are you sure you want ABORT merge?", modulesInMergingState.Select(x => x.Name).Join(", "), "Yes", "No"))
                    {
                        tasksInProgress.AddRange(modules.Select(module => module.RunGit($"merge --abort")));
                    }
                }

                tab = moduleNames.Length > 1 ? GUILayout.Toolbar(tab, moduleNames) : 0;
                var module = modules[tab];
                var unstagedSelection = selection[tab].unstaged;
                var stagedSelection = selection[tab].staged;

                GUILayout.Label($"{module.Name} [{module.CurrentBranch.GetResultOrDefault() ?? ".."}]");

                if (module.GitRepoPath.GetResultOrDefault() is { } gitRepoPath && module.GitStatus.GetResultOrDefault() is { } status)
                {
                    var scrollHeight = GUILayout.Height(window.position.height - TopPanelHeight - BottomPanelHeight);
                    var scrollWidth = GUILayout.Width((window.position.width - MiddlePanelWidth) / 2);

                    using (new EditorGUI.DisabledGroupScope(tasksInProgress.Any()))
                    using (new GUILayout.HorizontalScope())
                    {
                        var unstaged = status.Unstaged.Where(x => showHidden || !x.Hidden);
                        void ShowUnstagedContextMenu(FileStatus file) => _ = ShowContextMenu(module, unstaged.Where(x => unstagedSelection.Contains(x.FullPath)));
                        GUIShortcuts.DrawList(unstaged, unstagedSelection, false, ShowUnstagedContextMenu, scrollHeight, scrollWidth);
                        using (new GUILayout.VerticalScope())
                        {
                            if (GUILayout.Button(">>", GUILayout.Width(MiddlePanelWidth)))
                            {
                                tasksInProgress.Add(module.RunGit($"add -f -- {PackageShortcuts.JoinFileNames(unstagedSelection)}"));
                                unstagedSelection.Clear();
                            }
                            if (GUILayout.Button("<<", GUILayout.Width(MiddlePanelWidth)))
                            {
                                tasksInProgress.Add(module.RunGit($"reset -q -- {PackageShortcuts.JoinFileNames(stagedSelection)}"));
                                stagedSelection.Clear();
                            }
                        }
                        var staged = status.Staged.Where(x => showHidden || !x.Hidden);
                        void ShowStagedContextMenu(FileStatus file) => _ = ShowContextMenu(module, staged.Where(x => stagedSelection.Contains(x.FullPath)));
                        GUIShortcuts.DrawList(staged, stagedSelection, true, ShowStagedContextMenu, scrollHeight, scrollWidth);
                    }
                }
                showHidden = GUILayout.Toggle(showHidden, "Show Hidden");
            });
            await Task.WhenAll(tasksInProgress.Where(x => x != null));
        }

        static async Task ShowContextMenu(Module module, IEnumerable<FileStatus> files)
        {
            if (!files.Any())
                return;
            Task task = null;
            GenericMenu menu = new GenericMenu();
            string filesList = PackageShortcuts.JoinFileNames(files.Select(x => x.FullPath));
            if (files.Any(x => x.IsInIndex))
            {
                menu.AddItem(new GUIContent("Diff"), false, () => task = Task.WhenAll(
                    Diff.ShowDiff(module, files.Where(x => x.IsStaged).Select(x => x.FullPath), true),
                    Diff.ShowDiff(module, files.Where(x => x.IsUnstaged).Select(x => x.FullPath), false)
                ));
                if (files.Any(x => x.IsUnstaged))
                {
                    menu.AddItem(new GUIContent("Discrad"), false, () => {
                        if (EditorUtility.DisplayDialog($"Are you sure you want DISCARD these files", filesList, "Yes", "No"))
                            task = module.RunGit($"checkout -q -- {filesList}");
                    });
                }
                if (files.Any(x => x.IsStaged))
                {
                    menu.AddItem(new GUIContent("Unstage"), false, () => {
                        task = module.RunGit($"reset -q -- {filesList}");
                    });
                }
            }
            
            string conflictedFilesList = PackageShortcuts.JoinFileNames(files.Where(x => x.IsUnresolved).Select(x => x.FullPath));
            if (!string.IsNullOrEmpty(conflictedFilesList))
            {
                menu.AddItem(new GUIContent("Take Ours"), false, () => {
                    if (EditorUtility.DisplayDialog($"Do you want to take OURS changes (git checkout --ours --)", conflictedFilesList, "Yes", "No"))
                        task = module.RunGit($"checkout --ours  -- {conflictedFilesList}");
                });
                menu.AddItem(new GUIContent("Take Theirs"), false, () => {
                    if (EditorUtility.DisplayDialog($"Do you want to take THEIRS changes (git checkout --theirs --)", conflictedFilesList, "Yes", "No"))
                        task = module.RunGit($"checkout --theirs  -- {conflictedFilesList}");
                });
            }
            menu.AddItem(new GUIContent("Delete"), false, () => {
                if (EditorUtility.DisplayDialog($"Are you sure you want DELETE these files", filesList, "Yes", "No"))
                {
                    foreach (var file in files)
                        File.Delete(file.FullPath);
                }
            });
            menu.ShowAsContext();
            if (task != null)
                await task;
        }
    }
}
