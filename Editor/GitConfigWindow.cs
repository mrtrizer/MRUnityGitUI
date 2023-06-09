using Abuksigun.MRGitUI;
using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class GitConfigWindow : MonoBehaviour
{
    record Setting(string name);

    static Setting[] settingsList = { new ("user.name"), new("user.email") };

    [MenuItem("Assets/Git/Config", true)]
    public static bool Check() => Utils.GetSelectedGitModules().Count() == 1;

    [MenuItem("Assets/Git/Config", priority = 100)]
    public static async void Invoke()
    {
        var module = Utils.GetSelectedGitModules().FirstOrDefault();
        if (module == null)
            return;
        var columnWidth = GUILayout.Width(200);
        var valueWidth = GUILayout.Width(160);
        var buttonWidth = GUILayout.Width(20);

        await GUIUtils.ShowModalWindow("Git Config", new Vector2Int(1000, 700), async window => {

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel("Name", columnWidth);
                foreach (var scope in Enum.GetValues(typeof(ConfigScope)).Cast<ConfigScope>())
                    EditorGUILayout.LabelField(scope.ToString(), columnWidth);
            }

            foreach (var setting in settingsList)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(setting.name, columnWidth);
                    foreach (var scope in Enum.GetValues(typeof(ConfigScope)).Cast<ConfigScope>())
                    {
                        var config = module.GitConfigValue(setting.name, scope).GetResultOrDefault();

                        if (scope != ConfigScope.None)
                        {
                            if (!string.IsNullOrEmpty(config) && GUILayout.Button("X", buttonWidth))
                                _ = module.UnsetConfig(setting.name, scope);
                            if (string.IsNullOrEmpty(config) ? GUILayout.Button("Set value", columnWidth) : GUILayout.Button("E", buttonWidth))
                                _ = ShowChangeSettingWindow(module, setting, scope);
                        }
                        if (!string.IsNullOrEmpty(config))
                            EditorGUILayout.SelectableLabel(config, valueWidth);
                    }
                }
            }
        });
    }

    static async Task ShowChangeSettingWindow(Module module, Setting setting, ConfigScope scope)
    {
        string newValue = await module.GitConfigValue(setting.name, scope);
        await GUIUtils.ShowModalWindow("Set Value", new Vector2Int(300, 180), window => {
            newValue = GUILayout.TextField(newValue);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Close"))
                {
                    window.Close();
                }
                if (GUILayout.Button("Apply"))
                {
                    _ = module.SetConfig(setting.name, scope, newValue);
                    window.Close();
                }
            }
        });
    }
}
