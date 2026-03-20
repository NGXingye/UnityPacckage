using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

public class FBXImporterWindow : OdinEditorWindow
{
    [MenuItem("Tools/FBX导入器")]
    public static void OpenFBXImporterWindow()
    {
        FBXImporterWindow window = GetWindow<FBXImporterWindow>();
        window.titleContent = new GUIContent("FBX导入器");
        window.Show();
    }

    private ResourcesSelectionWindow window;
    private bool createMirrorPrefab = false;
    private string mirrorPrefabName = "";
    private Vector2 fbxScrollPos;

    // --- 性能优化缓存变量 ---
    private List<string> cachedUniquePaths = new List<string>();
    private List<string> cachedDisplayNames = new List<string>(); // 缓存用于显示的短名称
    private double lastUpdateTime = 0;
    private Dictionary<string, string[]> dependencyCache = new Dictionary<string, string[]>(); // 依赖查询缓存

    #region --- 生命周期与轮询优化 ---
    
    protected override void OnEnable()
    {
        base.OnEnable();
        
        // 使用 EditorApplication.update 替代 Update()，大幅节省 CPU
        EditorApplication.update += Tick;
        
        window = EditorWindow.GetWindow<ResourcesSelectionWindow>();
        createMirrorPrefab = FbxConverter.GetCreateMirrorPrefab();
        mirrorPrefabName = FbxConverter.GetMirrorPrefabName();
        IsSkeleton = FbxConverter.GetIsSkeleton();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        EditorApplication.update -= Tick;
    }

    private void Tick()
    {
        if (EditorApplication.timeSinceStartup - lastUpdateTime > 0.2f)
        {
            lastUpdateTime = EditorApplication.timeSinceStartup;
            RefreshSelectionCache();
        }
    }

    private void RefreshSelectionCache()
    {
        if (window == null) return;
        List<string> rawPaths = window.GetSelectedFilePaths();
        
        if (rawPaths == null || rawPaths.Count == 0)
        {
            if (cachedUniquePaths.Count != 0)
            {
                cachedUniquePaths.Clear();
                cachedDisplayNames.Clear();
                Repaint();
            }
            return;
        }

        // 消除 LINQ 产生的巨量 GC，改用 HashSet 高效处理
        HashSet<string> set = new HashSet<string>();
        List<string> newPaths = new List<string>();
        List<string> newDisplayNames = new List<string>();

        foreach (var p in rawPaths)
        {
            if (string.IsNullOrEmpty(p) || p.EndsWith(".meta")) continue;

            string normalized = p.Replace("\\", "/").Trim();
            if (set.Add(normalized))
            {
                newPaths.Add(normalized);
                newDisplayNames.Add(Path.GetFileName(normalized)); //预先缓存文件名，减少渲染负担
            }
        }

        // 比对差异以决定是否 Repaint
        bool changed = newPaths.Count != cachedUniquePaths.Count;
        if (!changed)
        {
            for (int i = 0; i < newPaths.Count; i++)
            {
                if (newPaths[i] != cachedUniquePaths[i]) { changed = true; break; }
            }
        }

        if (changed)
        {
            cachedUniquePaths = newPaths;
            cachedDisplayNames = newDisplayNames;
            Repaint();
        }
    }
    #endregion

    #region --- 面板与 UI 绘制 ---

    [OnInspectorGUI][PropertyOrder(-10)]
    private void DrawOriginalTopUI()
    {
        EditorGUILayout.BeginVertical(GUI.skin.box);
        
        string currModelPath = window != null ? window.currModelPath : "";
        bool isAnimationMode = currModelPath.Contains("animations/");
        string modeText = "多级目录浏览模式";
        string workingModeText = isAnimationMode ? "动作编辑模式" : "资源提交模式";
        
        EditorGUILayout.LabelField($"当前资源选择模式: {modeText} ({workingModeText})", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"选中的文件路径 (共 {cachedUniquePaths.Count} 个)", EditorStyles.boldLabel);
        
        fbxScrollPos = EditorGUILayout.BeginScrollView(fbxScrollPos, GUILayout.Height(200));
        
        int maxDisplayCount = 50; 
        for (int i = 0; i < cachedUniquePaths.Count; i++)
        {
            if (i >= maxDisplayCount)
            {
                GUI.color = Color.gray;
                EditorGUILayout.LabelField($"... 以及其他 {cachedUniquePaths.Count - maxDisplayCount} 个文件已省略显示");
                GUI.color = Color.white;
                break;
            }
            // 优化 8：仅显示文件名，Tooltip 显示全路径，大幅减轻文本渲染压力
            EditorGUILayout.LabelField(new GUIContent("📄 " + cachedDisplayNames[i], cachedUniquePaths[i]));
        }
        
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    [Title("FBX设置")][LabelText("当前FBX是主骨架模型")][OnValueChanged("OnSkeletonSettingsChanged")]
    public bool IsSkeleton = false;[LabelText("当前传递为多mesh武器")]
    public bool IsWeapon = false;

    private void OnSkeletonSettingsChanged() { FbxConverter.SetIsSkeleton(IsSkeleton); }

    [OnInspectorGUI] private void DrawHelpBox() { EditorGUILayout.HelpBox("注意：在导入主骨架之前，如果某个大件fbx有修改，请先导入该大件", MessageType.Info); }

    private bool IsModelPath => window != null && !string.IsNullOrEmpty(window.currModelPath) && window.currModelPath.Contains("/Models/");
    
    [Title("动画覆盖与绑定设置")][ShowIf("IsModelPath")][LabelText("启用装备后缀")][ToggleLeft]
    public bool UseEquipment = false;[ShowIf("@IsModelPath && UseEquipment")][LabelText("装备后缀组合")]
    public string EquipSuffix = "#equip_c_main5_0007#equip_c_main6_0007";

    private bool isEquipRace;

    [Title("目标资源预览")][ShowIf("IsModelPath")][ShowInInspector, DisplayAsString(false), HideLabel]
    public string ExistingResourcesDisplay => GetFilteredResourcesText();
    
    [ButtonGroup("Actions")][Button("导入选中的FBX模型", ButtonSizes.Large), GUIColor(0.8f, 0.9f, 1f)]
    public void ImportSelectedFBX() { ImportFBXModels(cachedUniquePaths); }

    [ButtonGroup("Actions")][Button("更新并绑定资源文件", ButtonSizes.Large), GUIColor(0.6f, 1f, 0.6f)]
    [ShowIf("IsModelPath")] public void UpdateResourceFilesButton() { UpdateResourceFiles(); }

    [ButtonGroup("Actions")][Button("恢复默认角色资产", ButtonSizes.Large), GUIColor(1f, 0.6f, 0.6f)][ShowIf("IsModelPath")] public void RestoreDefaultAssetsButton() { RestoreDefaultAssets(); }

    [OnInspectorGUI][PropertyOrder(100)]
    private void DrawOriginalBottomUI()
    {
        if (window != null && !string.IsNullOrEmpty(window.currModelPath))
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("当前工作目录:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(window.currModelPath);
        }
    }
    #endregion

    #region --- 核心功能：导入、提取与转换 ---

    private void ImportFBXModels(List<string> filePaths)
    {
        if (filePaths == null || filePaths.Count == 0) return;

        FbxConverter.SetCreateMirrorPrefab(createMirrorPrefab);
        if (createMirrorPrefab && !string.IsNullOrEmpty(mirrorPrefabName))
            FbxConverter.SetMirrorPrefabName(mirrorPrefabName);

        Selection.objects = filePaths.Select(AssetDatabase.LoadAssetAtPath<Object>).ToArray();
        FbxConverter.SetForceReplaceMaterialsAndTextures(false);
        FbxConverter.ConvertSelectedFbxToPrefab(isWeapon: IsWeapon);

        HashSet<string> svnPaths = new HashSet<string>();
        dependencyCache.Clear();

        string basePath = window.currModelPath;
        bool isAnimationMode = basePath.Contains("animations/model") || basePath.Contains("animations/scene") ||
                               basePath.Contains("animations/ui") || basePath.Contains("animations/vfx");

        // 阶段 0：强制保底机制（放开给所有 Model 下的主骨架，因为怪物也需要 Avatar）
        if (!isAnimationMode && IsSkeleton && basePath.Contains("/Models/"))
        {
            foreach (var filePath in filePaths)
            {
                if (Path.GetFileNameWithoutExtension(filePath).Contains("@")) continue;

                ModelImporter importer = AssetImporter.GetAtPath(filePath) as ModelImporter;
                if (importer != null)
                {
                    bool needReimport = false;
                    if (importer.animationType == ModelImporterAnimationType.None)
                    {
                        importer.animationType = ModelImporterAnimationType.Generic;
                        needReimport = true;
                    }

                    if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                    {
                        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                        needReimport = true;
                    }

                    if (needReimport)
                    {
                        importer.SaveAndReimport();
                        Debug.Log($"[保底修复] 为 {Path.GetFileName(filePath)} 修复了 Avatar 设置！");
                    }
                }
            }
        }

        try
        {
            AssetDatabase.StartAssetEditing();

            if (!isAnimationMode)
            {
                if (TryParseModelPath(basePath, out string raceType, out string charDirRel, out string characterID))
                {
                    string targetBaseDir = "Assets/Resources/prefabs/model";
                    string targetRaceDir = $"{targetBaseDir}/{raceType}";
                    string targetCharDir = $"{targetBaseDir}/{charDirRel}"; // 使用嵌套相对路径！

                    EnsureDirectoryAndSVN(targetBaseDir, svnPaths);

                    // 【关键修复】逐层创建并提交中间文件夹，防止SVN提交丢失中间节点！
                    string currentBuildDir = targetBaseDir;
                    foreach (var part in charDirRel.Split('/'))
                    {
                        currentBuildDir += "/" + part;
                        EnsureDirectoryAndSVN(currentBuildDir, svnPaths);
                    }

                    if (raceType.Contains("_c")) EnsureDirectoryAndSVN($"{targetRaceDir}/masks", svnPaths);

                    // 种族模板生成 (0000_show)
                    string templateName = raceType.Contains("_h")
                        ? $"{raceType}_0000_show.controller"
                        : $"{raceType}_0000.controller";
                    string templatePath = $"{targetRaceDir}/{templateName}";
                    if (AssetDatabase.GetMainAssetTypeAtPath(templatePath) == null)
                        AnimatorController.CreateAnimatorControllerAtPath(templatePath);
                    AddDependenciesAndMetaToSVN(svnPaths, templatePath);

                    isEquipRace = raceType.Contains("character") || raceType.Contains("characterai");

                    string noMeshPath = $"{targetCharDir}/{characterID}.prefab";
                    string allPath = $"{targetCharDir}/{characterID}_all.prefab";
                    string avatarPath = $"{targetCharDir}/{characterID}_avatar.asset";
                    string overridePath = $"{targetCharDir}/{characterID}.overridecontroller";

                    foreach (var filePath in filePaths)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(filePath).ToLower();
                        if (fileName.Contains("@")) continue;
                        if (!IsSkeleton) continue;

                        GameObject fbxModel = AssetDatabase.LoadAssetAtPath<GameObject>(filePath);
                        if (fbxModel == null) continue;

                        // 【核心区分】：仅 _all 和剔除网格逻辑受 isEquipRace 限制
                        if (isEquipRace)
                        {
                            SavePrefabFromFBX(fbxModel, allPath, false); // 生成 _all
                            SavePrefabFromFBX(fbxModel, noMeshPath, true); // 生成剥离网格的 prefab
                        }
                        else
                        {
                            //处理网格
                            string targetMeshDir = $"Assets/Resources/meshs/model/{charDirRel}";
                            if (Directory.Exists(targetMeshDir))
                            {
                                // 清空目录下的所有旧网格，防止垃圾残留
                                string[] oldMeshes = Directory.GetFiles(targetMeshDir, "*.asset");
                                foreach (var om in oldMeshes) AssetDatabase.DeleteAsset(om.Replace("\\", "/"));
                            }

                            EnsureDirectoryAndSVN(targetMeshDir, svnPaths);
                            var smr = fbxModel.GetComponentInChildren<SkinnedMeshRenderer>(true);
                            Mesh sourceMesh = smr != null
                                ? smr.sharedMesh
                                : fbxModel.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;

                            if (sourceMesh != null)
                            {
                                Mesh newMesh = Object.Instantiate(sourceMesh);
                                newMesh.name = $"{characterID}_lod0";
                                string meshAssetPath = $"{targetMeshDir}/{characterID}_lod0.asset";
                                AssetDatabase.CreateAsset(newMesh, meshAssetPath);
                                AddDependenciesAndMetaToSVN(svnPaths, meshAssetPath);
                            }

                            SavePrefabFromFBX(fbxModel, noMeshPath, false); // 怪物：保留原网格
                            //强制清里all预制体
                            if (AssetDatabase.GetMainAssetTypeAtPath(allPath) != null)
                            {
                                AssetDatabase.DeleteAsset(allPath);
                            }
                        }

                        // 【所有种族都要有】Avatar 和 OverrideController
                        Avatar fbxAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(filePath);
                        if (fbxAvatar != null && AssetDatabase.GetMainAssetTypeAtPath(avatarPath) == null)
                            AssetDatabase.CreateAsset(Object.Instantiate(fbxAvatar), avatarPath);

                        if (AssetDatabase.GetMainAssetTypeAtPath(overridePath) == null)
                            AssetDatabase.CreateAsset(new AnimatorOverrideController(), overridePath);
                    }
                }
                else
                {
                    // UIs, Scenes, VFXs 通用提取逻辑
                    string catName = "";
                    string mapName = "";
                    if (basePath.Contains("/UIs/"))
                    {
                        catName = "UIs";
                        mapName = "ui";
                    }
                    else if (basePath.Contains("/Scenes/"))
                    {
                        catName = "Scenes";
                        mapName = "scene";
                    }
                    else if (basePath.Contains("/VFXs/"))
                    {
                        catName = "VFXs";
                        mapName = "vfx";
                    }

                    if (!string.IsNullOrEmpty(catName))
                    {
                        int catIndex = basePath.IndexOf($"/{catName}/", StringComparison.OrdinalIgnoreCase) +
                                       $"/{catName}/".Length;
                        string relPath = basePath.Substring(catIndex).ToLower();
                        string baseCatDir = $"Assets/Resources/prefabs/{mapName}";
                        string targetDir = $"{baseCatDir}/{relPath}";
                        EnsureDirectoryAndSVN(baseCatDir, svnPaths);
                        EnsureDirectoryAndSVN(targetDir, svnPaths);

                        foreach (var filePath in filePaths)
                        {
                            if (Path.GetFileNameWithoutExtension(filePath).Contains("@")) continue;
                            GameObject fbxModel = AssetDatabase.LoadAssetAtPath<GameObject>(filePath);
                            if (fbxModel != null)
                                SavePrefabFromFBX(fbxModel,
                                    $"{targetDir}/{Path.GetFileNameWithoutExtension(filePath).ToLower()}.prefab",
                                    false);
                        }
                    }
                }

                // 动画提取与 SVN 收集
                foreach (var filePath in filePaths)
                {
                    ModelImporter modelImporter = AssetImporter.GetAtPath(filePath) as ModelImporter;
                    if (modelImporter != null) FBXSetting.refreshModelImporterSetting(modelImporter);

                    AddDependenciesAndMetaToSVN(svnPaths, filePath);

                    string artRoot = "Assets/Art/FBX/Models/";
                    if (filePath.Contains("/UIs/")) artRoot = "Assets/Art/FBX/UIs/";
                    else if (filePath.Contains("/Scenes/")) artRoot = "Assets/Art/FBX/Scenes/";
                    else if (filePath.Contains("/VFXs/")) artRoot = "Assets/Art/FBX/VFXs/";

                    string outputPath = isAnimationMode
                        ? filePath.Replace("@", "_")
                        : filePath.Replace(artRoot, window.resourcePath).Replace("@", "_");
                    string animPath = outputPath.Replace(".fbx", ".anim");

                    if (AssetDatabase.GetMainAssetTypeAtPath(animPath) != null)
                        AddDependenciesAndMetaToSVN(svnPaths, animPath);
                    foreach (var asset in FindRelatedAssetsRoots(filePath))
                        AddDependenciesAndMetaToSVN(svnPaths, asset);
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        PushToSVNWindow("task:03 动画相关-导入动画资源及生成配套预制体", svnPaths);
        window.RefreshAssetList();
        window.Repaint();
        bool askUpdate = EditorUtility.DisplayDialog("导入成功",
            $"已导入 {filePaths.Count} 个FBX模型并抓取所有相关依赖送往 SVN。\n\n是否立即执行【更新并绑定资源文件】？",
            "立即更新绑定", "稍后手动执行");

        if (askUpdate) UpdateResourceFiles();
    }

    #endregion

    #region --- 核心功能：更新与自动绑定 ---
     private void UpdateResourceFiles()
    {
         string currentPath = window.currModelPath;
        if (!TryParseModelPath(currentPath, out string raceType, out string charDirRel, out string characterID))
        {
            EditorUtility.DisplayDialog("路径错误", "无法解析角色目录结构。", "确定"); return;
        }

        string targetRaceDir = $"Assets/Resources/prefabs/model/{raceType}";
        string targetCharDir = $"Assets/Resources/prefabs/model/{charDirRel}"; // 嵌套目录生效
        
        string noMeshPrefabPath = $"{targetCharDir}/{characterID}.prefab";
        string avatarPath = $"{targetCharDir}/{characterID}_avatar.asset";

        if (!Directory.Exists(targetCharDir) || AssetDatabase.GetMainAssetTypeAtPath(noMeshPrefabPath) == null)
        {
            EditorUtility.DisplayDialog("缺失默认资产", $"未检测到【{characterID}】的预制体资产。\n\n请先选中 FBX 执行导入！", "去导入"); return;
        }

        HashSet<string> svnPaths = new HashSet<string>();
        dependencyCache.Clear();

        try
        {
            AssetDatabase.StartAssetEditing();

            string templateName = raceType.Contains("_h") ? $"{raceType}_0000_show.controller" : $"{raceType}_0000.controller";
            string templatePath = $"{targetRaceDir}/{templateName}";
            //目标状态机，完整路径
            string tagetId = $"{characterID}.controller";
            string tagetname = $"{targetRaceDir}/{tagetId}";
            AnimatorController tagetController = AssetDatabase.LoadAssetAtPath<AnimatorController>(tagetname);
            //默认
            AnimatorController originalController = AssetDatabase.LoadAssetAtPath<AnimatorController>(templatePath);
            if (originalController == null) { EditorUtility.DisplayDialog("错误", $"找不到种族模板状态机: {templatePath}", "确定"); return; }

            string[] overrideFiles = Directory.GetFiles(targetCharDir, "*.overridecontroller");
            if (overrideFiles.Length == 0) Debug.LogWarning($"[提示] 【{characterID}】目录下未找到任何 OverrideController！");

            string defaultOverrideName = $"{characterID}.overridecontroller";

            foreach (var file in overrideFiles)
            {
                string opath = file.Replace("\\", "/");
                string fileName = Path.GetFileName(opath);
                AnimatorOverrideController overrideCtrl = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(opath);
                if (overrideCtrl == null) continue;

                overrideCtrl.runtimeAnimatorController = tagetController;
                
                if (overrideCtrl.runtimeAnimatorController == null)
                {
                    if (EditorUtility.DisplayDialog("缺少模板绑定", $"检测到 OverrideController【{fileName}】未绑定基础控制器！是否自动绑定并继续填充？", "自动绑定", "跳过"))
                        overrideCtrl.runtimeAnimatorController = originalController;
                    else continue;
                }
                AnimatorController baseController = overrideCtrl.runtimeAnimatorController as AnimatorController ?? originalController;
                bool isDefaultOverride = fileName.Equals(defaultOverrideName, StringComparison.OrdinalIgnoreCase);
                bool shouldApplyEquipSuffix = UseEquipment && !isDefaultOverride;

                // 注意这里传入的是 charDirRel (完整嵌套路径)
                AutoBindAnimations(overrideCtrl, baseController, charDirRel, characterID, shouldApplyEquipSuffix);
                EditorUtility.SetDirty(overrideCtrl);
                AddDependenciesAndMetaToSVN(svnPaths, opath);
            }

            AnimatorOverrideController defaultOverride = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>($"{targetCharDir}/{defaultOverrideName}");
            if (defaultOverride == null && overrideFiles.Length > 0) defaultOverride = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(overrideFiles[0].Replace("\\", "/"));

            GameObject prefabContents = PrefabUtility.LoadPrefabContents(noMeshPrefabPath);
            Animator animator = prefabContents.GetComponent<Animator>();
            if (animator == null) animator = prefabContents.AddComponent<Animator>();

            if (defaultOverride != null) animator.runtimeAnimatorController = defaultOverride;
            animator.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);

            if (!isEquipRace)
            {
               
                string targetMeshDir = $"Assets/Resources/meshs/model/{charDirRel}";
                string meshAssetPath = $"{targetMeshDir}/{characterID}_lod0.asset";
                string matAssetPath = $"Assets/Resources/materials/model/{charDirRel}/{characterID}.mat";//材质存放路径
                Mesh extractedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
                Material extractedMat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
                var smr = prefabContents.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null)
                {
                    if (extractedMesh != null) smr.sharedMesh = extractedMesh;
                    if (extractedMat != null) smr.sharedMaterials = new Material[] { extractedMat };
                    else Debug.LogWarning($"[提示] 未在 {matAssetPath} 找到材质，请确保材质资产已存在！");
                }
                else
                {
                    // 兼容少数使用普通 MeshFilter 甚至挂载了武器的情况
                    var mf = prefabContents.GetComponentInChildren<MeshFilter>(true);
                    var mr = prefabContents.GetComponentInChildren<MeshRenderer>(true);
                    if (mf != null && extractedMesh != null) mf.sharedMesh = extractedMesh;
                    
                    // 使用数组赋值材质 ---
                    if (mr != null && extractedMat != null) mr.sharedMaterials = new Material[] { extractedMat };
                }
            }
            else
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            animator.applyRootMotion = true;
            PrefabUtility.SaveAsPrefabAsset(prefabContents, noMeshPrefabPath);
            PrefabUtility.UnloadPrefabContents(prefabContents);

            AddDependenciesAndMetaToSVN(svnPaths, noMeshPrefabPath);
            AddDependenciesAndMetaToSVN(svnPaths, avatarPath);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        PushToSVNWindow("task:03 动画相关-更新绑定角色资源", svnPaths);
        EditorUtility.DisplayDialog("成功", $"【{characterID}】的预制体绑定及 Override 自动匹配已完成！", "确定");
    }
   // 签名改变：raceType 变成了 charDirRel，以支持多级嵌套下的动画精准获取
    private void AutoBindAnimations(AnimatorOverrideController overrideController, AnimatorController originalController, string charDirRel, string characterID, bool applyEquipSuffix)
    {
        string animRootPath = $"Assets/Resources/animations/model/{charDirRel}"; // 使用完整嵌套路径寻找动画
        if (!Directory.Exists(animRootPath)) return;

        List<KeyValuePair<AnimationClip, AnimationClip>> overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        int successCount = 0;

        foreach (var originalClip in originalController.animationClips)
        {
            if (originalClip == null || !originalClip.name.StartsWith("placeholder_")) continue;

            string rawType = originalClip.name.Replace("placeholder_", "");
            bool isMinor = rawType.Contains("_minor");
            string actualType = isMinor ? rawType.Replace("_minor", "") : rawType;

            string suffix = (!isMinor && applyEquipSuffix && !string.IsNullOrEmpty(EquipSuffix)) ? EquipSuffix : "";
            
            string targetClipName = $"{characterID}_{actualType}{suffix}";
            string fullAnimPath = $"{animRootPath}/{targetClipName}.anim";
            
            AnimationClip targetClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(fullAnimPath);

            if (targetClip == null && applyEquipSuffix && !isMinor)
            {
                string fallbackClipName = $"{characterID}_{actualType}";
                string fallbackPath = $"{animRootPath}/{fallbackClipName}.anim";
                targetClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(fallbackPath);
            }
            // 自动设置循环
            AutoSetLoop(targetClip);
            overrides.Add(new KeyValuePair<AnimationClip, AnimationClip>(originalClip, targetClip));
            if (targetClip != null) successCount++;
        }

        if (overrides.Count > 0)
        {
            overrideController.ApplyOverrides(overrides);
            Debug.Log($"[自动绑定] 成功为 Override【{overrideController.name}】匹配了 {successCount} 个动画！");
        }
    }
    //设置动画loop
    private void AutoSetLoop(AnimationClip clip)
    {
        if (clip == null) return;

        string name = clip.name.ToLower();
        if (name.Contains("idle") || name.Contains("walk") || name.Contains("run"))
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            if (!settings.loopTime)
            {
                settings.loopTime = true;
                settings.loopBlend = true; // Loop Pose
                AnimationUtility.SetAnimationClipSettings(clip, settings);

                EditorUtility.SetDirty(clip);

                Debug.Log($"[自动Loop] {clip.name}");
            }
        }
    }
    #endregion

    #region --- 工具方法与性能重构辅助 ---

    // 零 GC 字符串解析
    private bool TryParseModelPath(string path, out string raceType, out string charDirRel, out string charID)
    {
        raceType = ""; charDirRel = ""; charID = "";
        if (string.IsNullOrEmpty(path)) return false;

        const string key = "/Models/";
        int index = path.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index == -1) return false;

        // 获取 Models 之后的所有相对路径部分 (例如: equip_h/equip_h_main1/equip_h_main1_1010)
        string remain = path.Substring(index + key.Length);
        var parts = remain.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        raceType = parts[0].ToLower();             // 第一级永远是种族 (equip_h)
        charDirRel = remain.ToLower();             // 完整保留完整的相对路径结构
        charID = parts[parts.Length - 1].ToLower();// 最后一级永远是具体的资源 ID
        return true;
    }

    //消除冗余的 Prefab 生成代码
    private void SavePrefabFromFBX(GameObject fbx, string path, bool stripMesh)
    {
        // 1. 实例化 FBX 模型
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);

        // 【核心修复】：强行解开 Prefab Variant 关联！
        // 把从 FBX 实例化出来的对象彻底打散成普通 GameObject，断开对原始 FBX 预制体节点的强依赖
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        // 2. 剥离网格逻辑（如果需要）
        if (stripMesh)
        {
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { smr.sharedMesh = null; smr.sharedMaterials = new Material[0]; }
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true)) mf.sharedMesh = null;
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true)) mr.sharedMaterials = new Material[0];
        }

        // 3. 此时保存的将是一个绝对纯净的普通 Prefab，再也不会出现 Broken 报错！
        PrefabUtility.SaveAsPrefabAsset(instance, path);
        DestroyImmediate(instance);
    }

    // 使用 AssetDatabase 内存验证代替 File.Exists 磁盘 IO
    private List<string> FindRelatedAssetsRoots(string fbxPath)
    {
        List<string> results = new List<string>();
        string fileName = Path.GetFileNameWithoutExtension(fbxPath).ToLower();
        if (fileName.Contains("@")) return results;
        
        string baseName = fileName.Split('@')[0];
        string typePath = ""; string rootPath = "";
        
        if (fbxPath.Contains("/Scenes/")) { typePath = "scene"; rootPath = "Assets/Art/FBX/Scenes"; }
        else if (fbxPath.Contains("/Models/")) { typePath = "model"; rootPath = "Assets/Art/FBX/Models"; }
        else if (fbxPath.Contains("/UIs/")) { typePath = "ui"; rootPath = "Assets/Art/FBX/UIs"; }
        else if (fbxPath.Contains("/VFXs/")) { typePath = "vfx"; rootPath = "Assets/Art/FBX/VFXs"; }
        
        if (string.IsNullOrEmpty(rootPath) || !fbxPath.StartsWith(rootPath)) return results;
        
        string relPath = fbxPath.Substring(rootPath.Length + 1);
        relPath = relPath.Substring(0, relPath.LastIndexOf('/')).ToLower();
        string basePrefab = $"Assets/Resources/prefabs/{typePath}/{relPath}/{baseName}.prefab";
        
        if (AssetDatabase.GetMainAssetTypeAtPath(basePrefab) != null) results.Add(basePrefab);
        
        if (typePath == "model")
        {
            // 仅换装种族去抓取 _all.prefab
            bool isEquipRace = relPath.Contains("character") || relPath.Contains("characterai");
            if (isEquipRace && IsSkeleton && AssetDatabase.GetMainAssetTypeAtPath(basePrefab.Replace(".prefab", "_all.prefab")) != null) 
            {
                results.Add(basePrefab.Replace(".prefab", "_all.prefab"));
            }

            // 【所有种族通用】：Avatar 和 OverrideController
            if (AssetDatabase.GetMainAssetTypeAtPath(basePrefab.Replace(".prefab", "_avatar.asset")) != null) 
                results.Add(basePrefab.Replace(".prefab", "_avatar.asset"));
            if (AssetDatabase.GetMainAssetTypeAtPath(basePrefab.Replace(".prefab", ".overridecontroller")) != null) 
                results.Add(basePrefab.Replace(".prefab", ".overridecontroller"));
        }
        return results;
    }

    // 全局依赖查询缓存，消除巨量重复递归扫描
    private string[] GetDependenciesCached(string path)
    {
        if (!dependencyCache.TryGetValue(path, out var deps))
        {
            deps = AssetDatabase.GetDependencies(path, true);
            dependencyCache[path] = deps;
        }
        return deps;
    }

    private void AddDependenciesAndMetaToSVN(HashSet<string> list, string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == null) return;
        
        foreach (var d in GetDependenciesCached(path)) 
        {
            string p = d.Replace("\\", "/");
            
            // 白名单拦截！只允许 Assets/ 目录下的文件进入 SVN 提交列表
            // 彻底杜绝 Packages/ 目录（如 URP Shader、PBR 插件包）被意外抓取！
            if (!p.StartsWith("Assets/")) 
                continue;

            list.Add(p);
            
            if (File.Exists(p + ".meta")) 
                list.Add(p + ".meta");
        }
    }

    private void EnsureDirectoryAndSVN(string path, HashSet<string> svnPaths)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        string normalized = path.Replace("\\", "/");
        svnPaths.Add(normalized);
        if (File.Exists(normalized + ".meta")) svnPaths.Add(normalized + ".meta");
    }

    private void PushToSVNWindow(string msg, HashSet<string> paths)
    {
        var w = GetWindow<TortoiseSVNCommitWindow>();
        w.commitMessage = msg;
        w.commitPaths.Clear();
        w.commitPaths.AddRange(paths.ToList());
    }

    private void RestoreDefaultAssets()
    {
        if (!TryParseModelPath(window.currModelPath, out string raceType, out string charDirRel, out string charID)) return;

        string targetCharDir = $"Assets/Resources/prefabs/model/{charDirRel}";
        if (!Directory.Exists(targetCharDir)) return;
        if (!EditorUtility.DisplayDialog("高危操作警告", $"确定清理目录非默认资产吗？", "确定", "取消")) return;

        HashSet<string> defaultAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { $"{charID}.prefab", $"{charID}_avatar.asset", $"{charID}.overridecontroller" };
        
        bool isEquipRace = raceType.Contains("character") || raceType.Contains("characterai");
        if (isEquipRace) defaultAssets.Add($"{charID}_all.prefab");
        
        HashSet<string> svnDeletedPaths = new HashSet<string>();
        int deleteCount = 0;

        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var file in Directory.GetFiles(targetCharDir))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (!defaultAssets.Contains(Path.GetFileName(file)))
                {
                    string assetPath = file.Replace("\\", "/");
                    if (AssetDatabase.DeleteAsset(assetPath))
                    {
                        svnDeletedPaths.Add(assetPath);
                        svnDeletedPaths.Add(assetPath + ".meta");
                        deleteCount++;
                    }
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }
        
        if (deleteCount > 0) PushToSVNWindow("task:03 动画相关-清理废旧角色资产", svnDeletedPaths);
        EditorUtility.DisplayDialog("清理完成", $"清理了 {deleteCount} 个文件！", "确定");
    }

    private string GetFilteredResourcesText()
    {
        if (!TryParseModelPath(window?.currModelPath, out string rType, out string charDirRel, out string cID)) return "未选中有效目录";
        
        string tRace = $"Assets/Resources/prefabs/model/{rType}";
        string tChar = $"Assets/Resources/prefabs/model/{charDirRel}"; // 使用完整嵌套路径

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"[预估资产目录]: {tChar}\n");
        string[] exts = { ".prefab", ".controller", ".overridecontroller" };
        bool hasRes = false;

        Action<string, string> AppendDir = (dir, title) => {
            if (Directory.Exists(dir)) {
                sb.AppendLine($"[{title}] :");
                foreach (var f in Directory.GetFiles(dir)) {
                    string e = Path.GetExtension(f).ToLower();
                    if (exts.Contains(e) || f.EndsWith("_avatar.asset", StringComparison.OrdinalIgnoreCase)) {
                        sb.AppendLine("    📄 " + Path.GetFileName(f));
                        hasRes = true;
                    }
                }
            }
        };
        AppendDir(tRace, "种族级"); AppendDir(tChar, "角色级");
        if (!hasRes) sb.AppendLine("⚠ 暂无资产，需导入 FBX。");
        return sb.ToString();
    }
    #endregion
}