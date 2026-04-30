using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;

public class MonsterAnimatorToolOdin : OdinEditorWindow
{
    [MenuItem("Tools/Animator/怪物模板状态机批量生成工具")]
     private static void OpenWindow()
    {
        var window = GetWindow<MonsterAnimatorToolOdin>("怪物状态机工具");
        window.position = new Rect(100, 100, 700, 800);
        window.Show();
    }
     [TitleGroup("基础设置", "设置路径和模板资源", alignment: TitleAlignments.Centered)]
     [FolderPath(RequireExistingPath = true)]
    [LabelText("种族文件夹路径")][OnValueChanged("RefreshData")]
    public string raceFolderPath;

    [TitleGroup("基础设置")][LabelText("默认模板状态机")]
    [OnValueChanged("RefreshData")]
    public AnimatorController templateController;

    // 隐藏在面板中，仅供下方的 ValueDropdown 调用作为数据源
    [HideInInspector]
    public List<string> maskSuffixes = new List<string>();

    // 遮罩资源缓存字典，使用 OrdinalIgnoreCase 忽略大小写！防止手误导致匹配失败。
    private Dictionary<string, AvatarMask> allMasksDict = new Dictionary<string, AvatarMask>(StringComparer.OrdinalIgnoreCase);
   
    [TitleGroup("层级与遮罩绑定设置", "检测模板中的Layer，勾选并分配对应后缀")]
    [TableList(IsReadOnly = true, AlwaysExpanded = true)]
    [LabelText("层级列表")]
    public List<LayerConfig> layerConfigs = new List<LayerConfig>();
    
    [System.Serializable]
    public class LayerConfig
    {
        [TableColumnWidth(150, Resizable = false)]
        [ReadOnly]
        [LabelText("层级名称")]
        public string layerName;

        [TableColumnWidth(80, Resizable = false)]
        [LabelText("绑定遮罩")]
        public bool bindMask;

        [ShowIf("bindMask")]
        [ValueDropdown("@$root.maskSuffixes")]
        [LabelText("选择遮罩后缀")]
        public string selectedSuffix;
    }

    [TitleGroup("操作区", alignment: TitleAlignments.Centered)]
    [Button("刷新数据 / 重新提取遮罩", ButtonSizes.Medium)][PropertySpace(SpaceBefore = 10, SpaceAfter = 10)]
    private void RefreshData()
    {
        ParseLayers();
        ParseMasks();
        RefreshGeneratedList();
    }

    [TitleGroup("操作区")]
    [Button("批量复刻并设置Mask", ButtonSizes.Gigantic)][GUIColor(0.2f, 1f, 0.2f)][PropertySpace(SpaceAfter = 20)]
    private void ExecuteBatchProcess()
    {
        if (string.IsNullOrEmpty(raceFolderPath) || templateController == null)
        {
            EditorUtility.DisplayDialog("错误", "请确保路径和模板状态机已设置正确！", "确定");
            return;
        }

        string templatePath = AssetDatabase.GetAssetPath(templateController);
        string templateSysPath = Path.GetFullPath(templatePath);

        string relativeRacePath = raceFolderPath;
        if (relativeRacePath.StartsWith(Application.dataPath))
        {
            relativeRacePath = "Assets" + relativeRacePath.Substring(Application.dataPath.Length);
        }
        
        // 防止代码重新编译或放久了导致内存字典被自动清空，从而找不到Mask。
        ParseMasks(); 

        string[] characterDirs = Directory.GetDirectories(relativeRacePath);
        int processCount = 0;

        try
        {
            for (int i = 0; i < characterDirs.Length; i++)
            {
                string dir = characterDirs[i].Replace("\\", "/");
                string characterName = Path.GetFileName(dir);

                if (characterName.ToLower() == "masks" || characterName.StartsWith(".")) continue;

                string targetAssetPath = $"{relativeRacePath}/{characterName}.controller";
                string targetSysPath = Path.GetFullPath(targetAssetPath);

                EditorUtility.DisplayProgressBar("处理中", $"正在生成 {characterName}.controller...", (float)i / characterDirs.Length);

                // 复制并保证GUID不变
                File.Copy(templateSysPath, targetSysPath, true);
                AssetDatabase.ImportAsset(targetAssetPath, ImportAssetOptions.ForceUpdate);

                // 修改子状态机的内部数据
                AnimatorController targetController = AssetDatabase.LoadAssetAtPath<AnimatorController>(targetAssetPath);
                if (targetController != null)
                {
                    bool changed = false;

                    if (targetController.name != characterName)
                    {
                        targetController.name = characterName;
                        changed = true;
                    }

                    AnimatorControllerLayer[] layers = targetController.layers;

                    for (int j = 0; j < layers.Length; j++)
                    {
                        var layerConf = layerConfigs.FirstOrDefault(c => c.layerName == layers[j].name);
                        
                        if (layerConf != null && layerConf.bindMask && !string.IsNullOrEmpty(layerConf.selectedSuffix))
                        {
                            string expectedMaskName = $"{characterName}_{layerConf.selectedSuffix}";

                            // 【核心修复3】：有该角色的Mask就设置，没有就不设置
                            if (allMasksDict.TryGetValue(expectedMaskName, out AvatarMask targetMask))
                            {
                                layers[j].avatarMask = targetMask;
                                changed = true;
                                Debug.Log($"<color=green>[成功绑定]</color> 为角色 {characterName} 的 {layers[j].name} 绑定了遮罩：{targetMask.name}");
                            }
                            else
                            {
                                // 没有对应的Mask -> 不设置，仅打印提示
                                Debug.Log($"<color=orange>[跳过绑定]</color> 角色 {characterName} 缺少对应遮罩资源({expectedMaskName})，已保持默认/空状态。");
                            }
                        }
                    }

                    if (changed)
                    {
                        targetController.layers = layers; // 必须将数组重新赋值回去才能生效
                        EditorUtility.SetDirty(targetController);
                    }
                }
                
                processCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            RefreshGeneratedList();
            
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("完成", $"成功处理了 {processCount} 个角色的子模板状态机！请查看Console控制台确认Mask绑定情况。", "确定");
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError(e);
            EditorUtility.DisplayDialog("错误", "处理过程中发生错误，请查看控制台日志。", "确定");
        }
    }
    [TitleGroup("生成结果", "当前目录下存在的子状态机（点击可定位或在此直接修改）", alignment: TitleAlignments.Centered)]
    [LabelText("已生成的子状态机列表")]
    [ListDrawerSettings(IsReadOnly = true, ShowPaging = false, Expanded = true)]
    public List<AnimatorController> generatedControllers = new List<AnimatorController>();

    private void RefreshGeneratedList()
    {
        generatedControllers.Clear();
        if (string.IsNullOrEmpty(raceFolderPath)) return;

        string relativeRacePath = raceFolderPath;
        if (relativeRacePath.StartsWith(Application.dataPath))
            relativeRacePath = "Assets" + relativeRacePath.Substring(Application.dataPath.Length);

        if (!AssetDatabase.IsValidFolder(relativeRacePath)) return;

        string[] characterDirs = Directory.GetDirectories(relativeRacePath);
        foreach (string dir in characterDirs)
        {
            string characterName = Path.GetFileName(dir);
            if (characterName.ToLower() == "masks" || characterName.StartsWith(".")) continue;

            string expectedControllerPath = $"{relativeRacePath}/{characterName}.controller";
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(expectedControllerPath);
            
            if (controller != null && controller != templateController) 
            {
                generatedControllers.Add(controller);
            }
        }
    }

    private void ParseLayers()
    {
        if (templateController == null)
        {
            layerConfigs.Clear();
            return;
        }

        var oldConfigs = new Dictionary<string, LayerConfig>();
        foreach (var config in layerConfigs)
        {
            oldConfigs[config.layerName] = config;
        }

        layerConfigs.Clear();
        foreach (var layer in templateController.layers)
        {
            if (oldConfigs.TryGetValue(layer.name, out LayerConfig existingConfig))
            {
                layerConfigs.Add(existingConfig);
            }
            else
            {
                layerConfigs.Add(new LayerConfig
                {
                    layerName = layer.name,
                    bindMask = false,
                    selectedSuffix = ""
                });
            }
        }
    }

    private void ParseMasks()
    {
        maskSuffixes.Clear();
        allMasksDict.Clear(); // 清空旧数据重新装载

        if (string.IsNullOrEmpty(raceFolderPath)) return;

        string relativeRacePath = raceFolderPath;
        if (relativeRacePath.StartsWith(Application.dataPath))
            relativeRacePath = "Assets" + relativeRacePath.Substring(Application.dataPath.Length);

        string masksFolderPath = relativeRacePath + "/masks";
        if (!AssetDatabase.IsValidFolder(masksFolderPath)) return;

        string[] characterDirs = Directory.GetDirectories(relativeRacePath)
            .Select(Path.GetFileName)
            .Where(name => name.ToLower() != "masks" && !name.StartsWith("."))
            .ToArray();

        string[] maskGuids = AssetDatabase.FindAssets("t:AvatarMask", new[] { masksFolderPath });
        
        foreach (string guid in maskGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
            if (mask != null)
            {
                // 将遮罩存入忽略大小写的字典
                allMasksDict[mask.name] = mask;

                string suffix = mask.name;
                foreach (string charName in characterDirs)
                {
                    string prefix = charName + "_";
                    // 提取后缀时也做一个安全的大小写忽略匹配
                    if (mask.name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    {
                        suffix = mask.name.Substring(prefix.Length);
                        break;
                    }
                }

                if (!maskSuffixes.Contains(suffix))
                {
                    maskSuffixes.Add(suffix);
                }
            }
        }
    }
}