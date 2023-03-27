using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Abuksigun.PackageShortcuts
{
    // Graphical log - https://github.com/pvigier/gitamine/blob/master/src/renderer/components/graph-canvas.tsx

    public static class GitLog
    {
        const int BottomPanelHeight = 200;
        const int FileListHeight = 150;

        static Lazy<GUIStyle> IdleLogStyle = new(() => new (Style.Idle.Value) { font = Style.MonospacedFont.Value, fontSize = 12, richText = true });
        static Lazy<GUIStyle> SelectedLogStyle = new(() => new (Style.Selected.Value) { font = Style.MonospacedFont.Value, fontSize = 12, richText = true });

        [MenuItem("Assets/Git Log", true)]
        public static bool Check() => PackageShortcuts.GetSelectedGitModules().Any();
        [MenuItem("Assets/Git Log", priority = 100)]
        public static async void Invoke()
        {
            await ShowLog(null, false);
        }
        public static async Task ShowLog(IEnumerable<string> filePaths, bool viewStash)
        {
            var scrollPosition = Vector2.zero;
            Task<CommandResult> logTask = null;
            Task<string> currentBranchTask = null;
            var selectionPerModule = new Dictionary<string, ListState>();
            // TODO: Use default tab selector
            int tab = 0;
            string selectedCommit = null;

            await GUIShortcuts.ShowModalWindow("Git Log", new Vector2Int(800, 650), (window) => {
                var modules = PackageShortcuts.GetSelectedGitModules();
                if (!modules.Any())
                    return;
                tab = modules.Count() > 1 ? GUILayout.Toolbar(tab, modules.Select(x => x.Name).ToArray()) : 0;

                var module = modules.Skip(tab).First();
                if (logTask == null || currentBranchTask != module.CurrentBranch)
                {
                    currentBranchTask = module.CurrentBranch;
                    string settings = viewStash ? "-g" : "--graph --abbrev-commit --decorate";
                    string filter = viewStash ? "refs/stash" : "--branches --remotes --tags";
                    logTask = module.RunGit($"log {settings} --format=format:\"%h - %an (%ar) <b>%d</b> %s\" {filter}");
                }
                var selection = selectionPerModule.GetValueOrDefault(module.Guid) ?? (selectionPerModule[module.Guid] = new ListState());
                var windowWidth = GUILayout.Width(window.position.width);
                using (var scroll = new GUILayout.ScrollViewScope(scrollPosition, windowWidth, GUILayout.Height(window.position.height - BottomPanelHeight)))
                {
                    if (logTask.GetResultOrDefault() is { } log)
                    {
                        foreach (var commit in log.Output.Trim().Split('\n'))
                        {
                            string commitHash = Regex.Match(commit, @"([0-9a-f]+) - ")?.Groups[1].Value;
                            var style = selectedCommit == commitHash ? SelectedLogStyle.Value : IdleLogStyle.Value;
                            if (GUILayout.Toggle(selectedCommit == commitHash, commit, style) && !string.IsNullOrEmpty(commitHash))
                            {
                                if (commitHash != selectedCommit)
                                    selection.Clear();
                                if (Event.current.button == 1 && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                                    _ = ShowCommitContextMenu(module, commitHash);
                                selectedCommit = commitHash;

                            }
                        }
                    }
                    scrollPosition = scroll.scrollPosition;
                }
                if (module.GitRepoPath.GetResultOrDefault() is { } gitRepoPath && module.DiffFiles($"{selectedCommit}~1", selectedCommit).GetResultOrDefault() is { } diffFiles)
                {
                    void ShowSelectionContextMenu(FileStatus file) => ShowFileContextMenu(module, selection, selectedCommit);
                    GUIShortcuts.DrawList(diffFiles, selection, true, ShowSelectionContextMenu, windowWidth, GUILayout.Height(FileListHeight));
                }
            });
        }
        static async Task ShowCommitContextMenu(Module module, string selectedCommit)
        {
            var menu = new GenericMenu();
            var commitReference = new[] { new Reference(selectedCommit, selectedCommit, selectedCommit) };
            var references = commitReference.Concat((await module.References).Where(x => (x is LocalBranch || x is Tag) && x.Hash.StartsWith(selectedCommit)));
            foreach (var reference in references)
            {
                var contextMenuname = reference.QualifiedName.Replace("/", "\u2215");
                menu.AddItem(new GUIContent($"Checkout/{contextMenuname}"), false, () => {
                    if (EditorUtility.DisplayDialog("Are you sure you want CHECKOUT to COMMIT", reference.QualifiedName, "Yes", "No"))
                        _ = module.RunGit($"checkout {reference.QualifiedName}");
                });
                menu.AddItem(new GUIContent($"Reset/{contextMenuname}"), false, () => {
                    if (EditorUtility.DisplayDialog("Are you sure you want RESET to COMMIT", reference.QualifiedName, "Yes", "No"))
                        _ = module.RunGit($"reset --soft {reference.QualifiedName}");
                });
                menu.AddItem(new GUIContent($"Reset Hard/{contextMenuname}"), false, () => {
                    if (EditorUtility.DisplayDialog("Are you sure you want RESET HARD to COMMIT.", reference.QualifiedName, "Yes", "No"))
                        _ = module.RunGit($"reset --hard {reference.QualifiedName}");
                });
            }
            menu.ShowAsContext();
        }
        static void ShowFileContextMenu(Module module, IEnumerable<string> files, string selectedCommit)
        {
            if (!files.Any())
                return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Diff"), false, () => _ = Diff.ShowDiff(module, files, false, $"{selectedCommit}~1", selectedCommit));
            menu.AddItem(new GUIContent($"Revert to this commit"), false, () => {
                if (EditorUtility.DisplayDialog("Are you sure you want REVERT file?", selectedCommit, "Yes", "No"))
                    _ = GUIShortcuts.RunGitAndErrorCheck(module, $"checkout {selectedCommit} -- {PackageShortcuts.JoinFileNames(files)}");
            });
            menu.ShowAsContext();
        }
    }
}