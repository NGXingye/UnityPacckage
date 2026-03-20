#if ODIN_INSPECTOR
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public class AvatarMaskBatchTool : OdinEditorWindow
{
    [MenuItem("Tools/Animator/AvatarMask 设置工具")]
    private static void OpenWindow()
    {
        var win = GetWindow<AvatarMaskBatchTool>("AvatarMask");
        // 设置初始窗口大小
        win.position = new Rect(win.position.x, win.position.y, 500, 800);
        win.Show();
    }
    // =============================================================================顶部配置区 (Top)=====================
    [BoxGroup("Top",false , Order = -1)]
    [PropertyOrder(-10)]
    [Required("必须拖入模型根节点")]
    [LabelText("目标模型")]
    [OnValueChanged("OnTargetModelChange")]
    public GameObject targetModel;

    // --- 状态机绑定 (放在一起) ---
    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-9)]
    [Title("状态机绑定", horizontalLine: false, bold: true)]
    [LabelText("目标状态机")]
    [Required("请指定 Animator Controller")]
    public AnimatorController targetController;

    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-8)]
    [ValueDropdown("GetAnimatorLayers")]
    [LabelText("目标 Layer")]
    [ShowIf("targetController")]
    public string targetLayerName;

    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-7)]
    [Button("绑定 Mask 到 Layer", ButtonSizes.Medium), GUIColor(0.4f, 0.6f, 1f)]
    [EnableIf("CanBind")]
    private void BindToAnimator()
    {
        if (!targetController) return;
        Undo.RecordObject(targetController, "Bind Mask");
        var layers = targetController.layers;
        bool found = false;
        for (int i = 0; i < layers.Length; i++)
        {
            if (layers[i].name == targetLayerName)
            {
                layers[i].avatarMask = currentMask;
                found = true;
                break;
            }
        }
        if (found)
        {
            targetController.layers = layers;
            EditorUtility.SetDirty(targetController);
            AssetDatabase.SaveAssets();
            ShowNotification(new GUIContent($"已绑定到: {targetLayerName}"));
        }
        else ShowNotification(new GUIContent("Layer 未找到"));
    }

    // --- 创建文件 (放在一起) ---
    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-6)]
    [Title("Mask 创建与选择", horizontalLine: false, bold: true)]
    [LabelText("地址预览")]
    [DisplayAsString(Overflow = false)]
    [ShowIf("targetModel")]
    public string pathPreview;

    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-5)]
    [LabelText("后缀名")]
    [ShowIf("targetModel")]
    [OnValueChanged("UpdatePathPreview")]
    public string fileSuffix = "upper";

    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-4)]
    [Button("创建新 Mask 文件", ButtonSizes.Medium), GUIColor(0.2f, 0.8f, 0.2f)]
    [ShowIf("CanCreateMask")]
    private void CreateNewMask()
    {
        if (string.IsNullOrEmpty(pathPreview)) UpdatePathPreview();
        if (string.IsNullOrEmpty(pathPreview)) return;
        string dir = Path.GetDirectoryName(pathPreview);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        AvatarMask newMask = new AvatarMask();
        AssetDatabase.CreateAsset(newMask, pathPreview);
        currentMask = newMask;
        ReadFromAvatarMask(currentMask);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(newMask);
        ShowNotification(new GUIContent("创建成功!"));
    }

    [BoxGroup("Top/核心配置")]
    [PropertyOrder(-3)]
    [LabelText("当前 Mask")]
    [OnValueChanged("OnMaskChanged")]
    [InfoBox("修改后请点击底部的保存按钮", InfoMessageType.Warning, "IsDirty")]
    public AvatarMask currentMask;
    
    //====================================================================== 中间：骨骼列表 ===========================
    private Vector2 _listScrollPos;    // 用于记录列表的滚动位置

    // 开始滚动视图 (插入在列表之前)
    [OnInspectorGUI]
    [PropertyOrder(0)]
    private void BeginListScroll()
    {
        // 绘制标题
        Sirenix.Utilities.Editor.SirenixEditorGUI.Title("骨骼层级列表", "", TextAlignment.Left, true);
        
        // 限制高度为 300~500
        _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos, GUILayout.MinHeight(300), GUILayout.MaxHeight(600));
    }

    // 列表本体
    [PropertyOrder(1)]
    [ListDrawerSettings(IsReadOnly = true, ShowIndexLabels = false, ShowItemCount = false)]
    [LabelText(" ")] // 隐藏标题，但保留折叠箭头
    [ShowIf("HasData")]
    public List<BoneNode> boneTree = new List<BoneNode>();

    // 结束滚动视图 (插入在列表之后)
    [OnInspectorGUI]
    [PropertyOrder(2)]
    private void EndListScroll()
    {
        if (!HasData && targetModel != null)
        {
            if (GUILayout.Button("加载骨骼结构", GUILayout.Height(40)))
            {
                OnTargetModelChange();
            }
        }
        EditorGUILayout.EndScrollView();
        
        // 绘制一条分割线
        Sirenix.Utilities.Editor.SirenixEditorGUI.HorizontalLineSeparator(2);
    }
    
  //===================================================================== 底部：操作区 (常驻显示)==========================
    [BoxGroup("Bottom", false, Order = 100)]
    [ToggleLeft,GUIColor(0.5f,1,0.6f)]
    [LabelText("启用递归勾选 (选中父级自动选中子级)")]
    public bool recursiveToggle = true;

    // --- 快捷功能一行排布 (ButtonGroup) ---
    [BoxGroup("Bottom/快捷操作与保存",true,true)]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")] // 只要 Group 名字一样，按钮就会自动水平排布
    [Button("全部展开")] 
    private void ExpandAll() => SetTreeExpansion(true);

    [BoxGroup("Bottom/快捷操作与保存")]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")]
    [Button("全部折叠")] 
    private void CollapseAll() => SetTreeExpansion(false);

    [BoxGroup("Bottom/快捷操作与保存")]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")]
    [Button("全选")] 
    void ToolSelectAll() => SetSelectionAll(true);

    [BoxGroup("Bottom/快捷操作与保存")]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")]
    [Button("全不选")] 
    void ToolDeselectAll() => SetSelectionAll(false);

    [BoxGroup("Bottom/快捷操作与保存")]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")]
    [Button("智能:上半身")] 
    void ToolUpper() => SelectByKeywords(new[] { "Spine", "Clavicle", "Neak", "Arm", "Hand","Shoulder"});

    [BoxGroup("Bottom/快捷操作与保存")]
    [ButtonGroup("Bottom/快捷操作与保存/Tools")]
    [Button("智能:下半身")] 
    void ToolLower() => SelectByKeywords(new[] { "Hip", "Foot", "Foot", "Toe","Thigh" });

    // --- 保存按钮 ---
    [BoxGroup("Bottom/快捷操作与保存")]
    [Button("保存修改到 Mask", ButtonSizes.Large), GUIColor(1f, 0.6f, 0.2f)]
    [EnableIf("currentMask")]
    private void SaveMaskData()
    {
        if (currentMask == null) return;
        var allNodes = GetAllNodes();
        SerializedObject so = new SerializedObject(currentMask);
        var elements = so.FindProperty("m_Elements");
        if (elements == null) elements = so.FindProperty("m_TransformPaths");

        elements.ClearArray();
        elements.arraySize = allNodes.Count;

        for (int i = 0; i < allNodes.Count; i++)
        {
            var node = allNodes[i];
            var elem = elements.GetArrayElementAtIndex(i);
            elem.FindPropertyRelative("m_Path").stringValue = node.path;
            elem.FindPropertyRelative("m_Weight").floatValue = node.isSelected ? 1.0f : 0.0f;
        }
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(currentMask);
        AssetDatabase.SaveAssets();
        IsDirty = false;
        ShowNotification(new GUIContent("Mask 已保存!"));
    }
    // ============================================================
    // 内部逻辑 
    // ============================================================
    private Transform _rootTransform;
    [HideInInspector] public Transform previewBone;
    private bool IsDirty = false; 
    private bool HasData => boneTree != null && boneTree.Count > 0;
    private bool CanCreateMask => targetModel != null && !string.IsNullOrEmpty(fileSuffix);
    private bool CanBind => currentMask != null && targetController != null && !string.IsNullOrEmpty(targetLayerName);

    private void OnTargetModelChange()
    {
        boneTree.Clear();
        previewBone = null;
        IsDirty = false;
        if (targetModel)
        {
            _rootTransform = targetModel.transform;
            var rootNode = CreateNode(_rootTransform, _rootTransform);
            boneTree.Add(rootNode);
            var anim = targetModel.GetComponent<Animator>();
            if (anim != null) targetController = anim.runtimeAnimatorController as AnimatorController;
            if (currentMask != null) ReadFromAvatarMask(currentMask);
        }
        UpdatePathPreview();
    }
    private void UpdatePathPreview() => pathPreview = GetAutoPath();
    private BoneNode CreateNode(Transform current, Transform root)
    {
        string path = AnimationUtility.CalculateTransformPath(current, root);
        var node = new BoneNode { name = current.name, transform = current, path = path, isSelected = false, editorWindow = this };
        foreach (Transform child in current) node.children.Add(CreateNode(child, root));
        return node;
    }
    private void OnMaskChanged() { if (currentMask != null) { ReadFromAvatarMask(currentMask); IsDirty = false; } }
    private void ReadFromAvatarMask(AvatarMask mask)
    {
        if (!HasData) return;
        var activePaths = new HashSet<string>();
        for (int i = 0; i < mask.transformCount; i++) { if (mask.GetTransformActive(i)) activePaths.Add(mask.GetTransformPath(i)); }
        var all = GetAllNodes();
        foreach (var node in all) node.isSelected = activePaths.Contains(node.path);
    }
    private List<BoneNode> GetAllNodes() { var list = new List<BoneNode>(); foreach (var n in boneTree) CollectNodes(n, list); return list; }
    private void CollectNodes(BoneNode node, List<BoneNode> list) { list.Add(node); foreach (var c in node.children) CollectNodes(c, list); }
    private string GetAutoPath()
    {
        if (!targetModel) return "";
        string path = AssetDatabase.GetAssetPath(targetModel);
        if (string.IsNullOrEmpty(path)) { var source = PrefabUtility.GetCorrespondingObjectFromSource(targetModel); if (source) path = AssetDatabase.GetAssetPath(source); }
        if (string.IsNullOrEmpty(path)) path = "Assets/Temp.prefab";
        string dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        return $"{dir}/{name}_{fileSuffix}.mask".Replace("\\", "/");
    }
    private void SetSelectionAll(bool state) { var all = GetAllNodes(); foreach (var n in all) n.isSelected = state; MarkDirty(); }
    private void SelectByKeywords(string[] keys) { var all = GetAllNodes(); foreach (var n in all) { if (keys.Any(k => n.name.ToLower().Contains(k.ToLower()))) { n.isSelected = true; if(recursiveToggle) SetRecursive(n, true); } } MarkDirty(); }
    private void SetRecursive(BoneNode p, bool s) { foreach(var c in p.children) { c.isSelected = s; SetRecursive(c, s); } }
    public void MarkDirty() { IsDirty = true; SceneView.RepaintAll(); }
    private IEnumerable<string> GetAnimatorLayers() { if (!targetController) return new string[] { "请先设置 Animator Controller" }; return targetController.layers.Select(l => l.name); }
    private void SetTreeExpansion(bool expanded) { foreach (var prop in this.PropertyTree.EnumerateTree(true)) { if (prop.ValueEntry != null && prop.ValueEntry.TypeOfValue == typeof(List<BoneNode>)) { prop.State.Expanded = expanded; } } this.Repaint(); }
    protected override void OnEnable() { base.OnEnable(); SceneView.duringSceneGui += OnSceneGUI; }
    protected override void OnDisable() { base.OnDisable(); SceneView.duringSceneGui -= OnSceneGUI; }
    private void OnSceneGUI(SceneView view) { if (!targetModel || !HasData) return; foreach (var node in boneTree) DrawGizmos(node); }
    private void DrawGizmos(BoneNode node)
    {
        if (!node.transform) return;
        Color color = node.transform == previewBone ? Color.cyan : (node.isSelected ? Color.green : new Color(1,0,0,0.2f));
        float size = node.transform == previewBone ? 0.05f : (node.isSelected ? 0.03f : 0.015f);
        Handles.color = color;
        Handles.SphereHandleCap(0, node.transform.position, Quaternion.identity, size, EventType.Repaint);
        if (node.transform.parent && node.transform.parent != _rootTransform.parent) Handles.DrawLine(node.transform.position, node.transform.parent.position);
        foreach (var c in node.children) DrawGizmos(c);
    }
}

// ============================================================
// 树节点 (Tree Node)
// ============================================================
[System.Serializable]
public class BoneNode
{
    [HideInInspector] public string name;
    [HideInInspector] public Transform transform;
    [HideInInspector] public string path;
    [HideInInspector] public AvatarMaskBatchTool editorWindow;

    [HorizontalGroup("H", Gap = 5)]
    [ToggleLeft, LabelText("$name")]
    [OnValueChanged("OnToggle")]
    public bool isSelected;

    [HorizontalGroup("H", Width = 40)]
    [Button("View", ButtonSizes.Small)]
    [GUIColor("@GetViewColor()")]
    private void View()
    {
        if (editorWindow && transform) {
            editorWindow.previewBone = transform;
            EditorGUIUtility.PingObject(transform);
            SceneView.RepaintAll();
        }
    }

    [Indent]
    [ListDrawerSettings(IsReadOnly = true, ShowIndexLabels = false, ShowItemCount = false)]
    [LabelText(" ")] 
    public List<BoneNode> children = new List<BoneNode>();

    private void OnToggle() { if (editorWindow) { if (editorWindow.recursiveToggle) SetRecursive(this, isSelected); editorWindow.MarkDirty(); } }
    private void SetRecursive(BoneNode p, bool s) { foreach (var c in p.children) { c.isSelected = s; SetRecursive(c, s); } }
    private Color GetViewColor() => (editorWindow && editorWindow.previewBone == transform) ? Color.cyan : Color.white;
}
#endif