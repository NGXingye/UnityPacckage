using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;

public class AnimatorStateMachineTool : OdinEditorWindow
{
    [MenuItem("Tools/Animator/ 状态机批量工具")]
    private static void OpenWindow()
    {
        GetWindow<AnimatorStateMachineTool>("Animator 批量工具").Show();
    }

    #region 核心配置
    [TitleGroup("1 基础设置", Alignment = TitleAlignments.Centered)]
    [BoxGroup("1 基础设置/Settings", ShowLabel = false)]
    [OnValueChanged(nameof(OnControllerChanged))][LabelText("目标状态机"), Required, InlineEditor(InlineEditorObjectFieldModes.Boxed)]
    public AnimatorController controller;[BoxGroup("1 基础设置/Settings")]
    [ValueDropdown(nameof(GetLayerNames))][OnValueChanged(nameof(OnLayerChanged))]
    [LabelText("选择动画层级")]
    public int selectedLayer;

    #endregion

    #region 状态选择
    [TitleGroup("2 选择状态", Alignment = TitleAlignments.Centered)]
   
    [HorizontalGroup("2 选择状态/Select", PaddingLeft = 5, PaddingRight = 5)]
    
    [BoxGroup("2 选择状态/Select/起始状态")]
    [ValueDropdown(nameof(GetStatesInLayer), IsUniqueList = true)]
    [ListDrawerSettings(Expanded = true)]
    [LabelText(" ")]
    public List<AnimatorState> sourceStates = new List<AnimatorState>();
   
    [BoxGroup("2 选择状态/Select/目标状态")]
    [ValueDropdown(nameof(GetStatesInLayer), IsUniqueList = true)]
    [ListDrawerSettings(Expanded = true)]
    [LabelText(" ")]
    public List<AnimatorState> targetStates = new List<AnimatorState>();


    #endregion

    #region 批量属性设置 (仅限起始状态)
    [TitleGroup("3 状态属性修改 (仅作用于起始状态)", Alignment = TitleAlignments.Centered)]
    [BoxGroup("3 状态属性修改 (仅作用于起始状态)/Properties", ShowLabel = false)]
    
    // ----- 播放速度 Speed -----
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Speed")]
    [LabelText("播放速度 (Speed)")] public float defaultSpeed = 1f;
   
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Speed")]
    [LabelText("绑定参数")] public bool useSpeedParameter;
   
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Speed")]
    [ShowIf("useSpeedParameter"), ValueDropdown(nameof(GetFloatParameterNames)), HideLabel]
    public string speedParameter;

    // ----- 周期偏移 Offset -----
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Offset")]
    [LabelText("周期偏移 (Offset)")] 
    public float defaultCycleOffset = 0f;
  
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Offset")]
    [LabelText("绑定参数")] public bool useCycleOffsetParameter;
    
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Offset")]
    [ShowIf("useCycleOffsetParameter"), ValueDropdown(nameof(GetFloatParameterNames)), HideLabel]
    public string cycleOffsetParameter;

    // ----- 镜像 Mirror -----
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Mirror")]
    [LabelText("镜像 (Mirror)")] 
    public bool defaultMirror = false;
   
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Mirror")]
    [LabelText("绑定参数")] 
    public bool useMirrorParameter;
   
    [HorizontalGroup("3 状态属性修改 (仅作用于起始状态)/Properties/Mirror")]
    [ShowIf("useMirrorParameter"), ValueDropdown(nameof(GetBoolParameterNames)), HideLabel]
    public string mirrorParameter;

    // ----- 写入默认值 -----
    [BoxGroup("3 状态属性修改 (仅作用于起始状态)/Properties"),HideLabel]
    [LabelText("写入默认值 (Write Defaults)")] 
    public bool defaultWriteDefaults = false;

    [BoxGroup("3 状态属性修改 (仅作用于起始状态)/Properties")]
    [Button("仅应用属性到 起始状态", ButtonSizes.Medium), GUIColor(0.4f, 0.8f, 1f)]
    public void ApplyStateProperties()
    {
        if (controller == null || sourceStates.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "请至少选择一个起始状态进行修改！", "确定");
            return;
        }

        Undo.RecordObjects(sourceStates.ToArray(), "Batch Edit Source State Properties");

        foreach (var state in sourceStates)
        {
            state.speed = defaultSpeed;
            state.speedParameterActive = useSpeedParameter;
            if (useSpeedParameter) state.speedParameter = speedParameter;

            state.cycleOffset = defaultCycleOffset;
            state.cycleOffsetParameterActive = useCycleOffsetParameter;
            if (useCycleOffsetParameter) state.cycleOffsetParameter = cycleOffsetParameter;

            state.mirror = defaultMirror;
            state.mirrorParameterActive = useMirrorParameter;
            if (useMirrorParameter) state.mirrorParameter = mirrorParameter;

            state.writeDefaultValues = defaultWriteDefaults;
            
            EditorUtility.SetDirty(state);
        }
        
        AssetDatabase.SaveAssets();
        Debug.Log($"成功修改了 {sourceStates.Count} 个起始状态的属性及参数绑定。");
    }

    #endregion

    #region 过渡线与条件设置
    [TitleGroup("4 过渡线配置与操作", Alignment = TitleAlignments.Centered)][BoxGroup("4 过渡线配置与操作/Settings", ShowLabel = false)]
    [LabelText("拥有退出时间 (Has Exit Time)")] public bool hasExitTime = false;
    
    [BoxGroup("4 过渡线配置与操作/Settings")][EnableIf("hasExitTime")]
    [LabelText("退出时间 (Exit Time)")] public float exitTime = 0.75f;[BoxGroup("4 过渡线配置与操作/Settings")]
    [LabelText("固定时长 (Fixed Duration)")] public bool hasFixedDuration = true;
    
    [BoxGroup("4 过渡线配置与操作/Settings")][LabelText("过渡时长 (Transition Duration)")] public float transitionDuration = 0.25f;[BoxGroup("4 过渡线配置与操作/Settings")]
    [LabelText("过渡偏移 (Transition Offset)")] public float transitionOffset = 0f;

    [BoxGroup("4 过渡线配置与操作/Settings")]
    [LabelText("需要设置的过渡条件"), ListDrawerSettings(ShowFoldout = true, Expanded = true)]
    public List<TransitionCondition> conditions = new List<TransitionCondition>();

    [System.Serializable]
    public class TransitionCondition
    {
        [ValueDropdown("@$root.GetAllParameterNames()")]
        [LabelText("参数名"), HorizontalGroup("Cond")]
        public string parameterName;[LabelText("逻辑"), HorizontalGroup("Cond", Width = 80)]
        public AnimatorConditionMode mode;

        [LabelText("阈值"), HorizontalGroup("Cond", Width = 60)]
        public float threshold;
    }

    // ========== 操作按钮分离 ==========
    [HorizontalGroup("4 过渡线配置与操作/Settings/Actions")]
    [Button("批量创建 新的过渡线", ButtonSizes.Large), GUIColor(0.4f, 1f, 0.4f)]
    public void CreateNewTransitions()
    {
        if (!ValidateStates()) return;

        Undo.RecordObject(controller, "Batch Create Transitions");
        int count = 0;

        foreach (var srcState in sourceStates)
        {
            foreach (var dstState in targetStates)
            {
                if (srcState == dstState) continue; // 防止连自己

                AnimatorStateTransition transition = srcState.AddTransition(dstState);
                ApplySettingsToTransition(transition);
                count++;
            }
        }
        
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log($"成功【新建】 {count} 条过渡线！");
    }

    [HorizontalGroup("4 过渡线配置与操作/Settings/Actions")][Button("批量更新 已有的过渡线", ButtonSizes.Large), GUIColor(1f, 0.8f, 0.4f)]
    public void UpdateExistingTransitions()
    {
        if (!ValidateStates()) return;

        Undo.RecordObject(controller, "Batch Update Transitions");
        int count = 0;

        foreach (var srcState in sourceStates)
        {
            bool isStateModified = false;
            foreach (var transition in srcState.transitions)
            {
                // 如果这条已有过渡线的终点在我们的“目标列表”中，则对其进行更新
                if (targetStates.Contains(transition.destinationState))
                {
                    ApplySettingsToTransition(transition);
                    EditorUtility.SetDirty(transition);
                    isStateModified = true;
                    count++;
                }
            }
            if (isStateModified)
            {
                EditorUtility.SetDirty(srcState);
            }
        }

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log($"成功【更新】了 {count} 条已有过渡线！");
    }

    private void ApplySettingsToTransition(AnimatorStateTransition transition)
    {
        transition.hasExitTime = hasExitTime;
        transition.exitTime = exitTime;
        transition.duration = transitionDuration;
        transition.hasFixedDuration = hasFixedDuration;
        transition.offset = transitionOffset;
        
        // 覆盖条件（先清空旧条件，再添加面板上的条件）
        transition.conditions = new AnimatorCondition[0];
        foreach (var cond in conditions)
        {
            if (!string.IsNullOrEmpty(cond.parameterName))
                transition.AddCondition(cond.mode, cond.threshold, cond.parameterName);
        }
    }

    private bool ValidateStates()
    {
        if (controller == null) return false;
        if (sourceStates.Count == 0 || targetStates.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "请至少各选择一个起始状态和目标状态。", "确定");
            return false;
        }
        return true;
    }

    #endregion

    #region 数据获取与内部逻辑

    private void OnControllerChanged()
    {
        if (controller != null)
        {
            selectedLayer = Mathf.Clamp(selectedLayer, 0, controller.layers.Length - 1);
        }
        ClearSelection();
    }

    private void OnLayerChanged()
    {
        ClearSelection();
    }

    private void ClearSelection()
    {
        sourceStates.Clear();
        targetStates.Clear();
    }

    private ValueDropdownList<int> GetLayerNames()
    {
        var list = new ValueDropdownList<int>();
        if (controller == null) return list;
        for (int i = 0; i < controller.layers.Length; i++)
        {
            list.Add(controller.layers[i].name, i);
        }
        return list;
    }

    private IEnumerable<ValueDropdownItem<AnimatorState>> GetStatesInLayer()
    {
        var list = new List<ValueDropdownItem<AnimatorState>>();
        if (controller == null || controller.layers.Length == 0) return list;
        
        selectedLayer = Mathf.Clamp(selectedLayer, 0, controller.layers.Length - 1);
        var stateMachine = controller.layers[selectedLayer].stateMachine;
        
        foreach (var childState in stateMachine.states)
        {
            list.Add(new ValueDropdownItem<AnimatorState>(childState.state.name, childState.state));
        }
        return list;
    }

    // --- 区分类型的参数获取 (为了提供更精准的下拉框) ---

    public IEnumerable<string> GetAllParameterNames()
    {
        if (controller != null) return controller.parameters.Select(p => p.name);
        return Enumerable.Empty<string>();
    }

    public IEnumerable<string> GetFloatParameterNames()
    {
        if (controller != null) 
            return controller.parameters.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.name);
        return Enumerable.Empty<string>();
    }

    public IEnumerable<string> GetBoolParameterNames()
    {
        if (controller != null) 
            return controller.parameters.Where(p => p.type == AnimatorControllerParameterType.Bool).Select(p => p.name);
        return Enumerable.Empty<string>();
    }

    [OnInspectorInit]
    private void OnInspectorInit()
    {
        if (conditions.Count == 0 && controller != null && controller.parameters.Length > 0)
            conditions.Add(new TransitionCondition { parameterName = controller.parameters[0].name });
    }

    #endregion
}