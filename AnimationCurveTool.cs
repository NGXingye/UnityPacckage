using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor.Animations;

public class AnimationCurveBatchTool : OdinEditorWindow
{
    [MenuItem("Tools/Animator/批量动画曲线编辑器")]
    private static void OpenWindow()
    {
        var window = GetWindow<AnimationCurveBatchTool>();
        window.titleContent = new GUIContent("批量曲线工具");
        window.Show();
    }

    // ==========================================
    // 目标动画列表区
    // ==========================================
    [BoxGroup("1. 目标动画池", true)][InfoBox("请在此添加需要批量处理的动画片段。你可以直接在 Project 窗口多选动画，然后点击下方按钮获取。")]
    [ListDrawerSettings(ShowItemCount = true, DraggableItems = true)]
    [LabelText("目标片段列表")]
    public List<AnimationClip> targetClips = new List<AnimationClip>();

    [BoxGroup("1. 目标动画池")][HorizontalGroup("1. 目标动画池/Actions")]
    [Button("一键获取 Project 选中动画", ButtonSizes.Medium), GUIColor(0.3f, 0.8f, 0.8f)]
    private void GetSelectedClips()
    {
        // 深度获取选中的所有 AnimationClip (包含子文件夹)
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.DeepAssets);
        foreach (var clip in clips)
        {
            if (!targetClips.Contains(clip) && !clip.name.StartsWith("__preview__"))
            {
                targetClips.Add(clip);
            }
        }
    }

    [BoxGroup("1. 目标动画池")][HorizontalGroup("1. 目标动画池/Actions")][Button("清空列表", ButtonSizes.Medium), GUIColor(0.8f, 0.3f, 0.3f)]
    private void ClearClips() => targetClips.Clear();


    // ==========================================
    // 批量曲线模板区
    // ==========================================
    [BoxGroup("2. 曲线模板配置",true)][InfoBox("注意：此处的横坐标 0~1 代表动画的 0%~100% 进度。下发时会自动根据每个动画的时长(秒)进行等比拉伸。")][ListDrawerSettings(ShowFoldout = true, ShowPaging = false, ShowItemCount = true, DraggableItems = true)]
    [LabelText("批量下发曲线")]
    public List<BatchCurveEntry> curveTemplates = new List<BatchCurveEntry>();

    [BoxGroup("2. 曲线模板配置")][HorizontalGroup("2. 曲线模板配置/Actions")][Button("添加新曲线模板", ButtonSizes.Large), GUIColor(0.8f, 0.6f, 0.4f)]
    private void AddNewCurveTemplate()
    {
        curveTemplates.Add(new BatchCurveEntry
        {
            propertyName = "TurnWeight",
            curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f))
        });
    }[BoxGroup("2. 曲线模板配置")]
    [HorizontalGroup("2. 曲线模板配置/Actions")][Button("从列表首个动画提取曲线", ButtonSizes.Large), GUIColor(0.4f, 0.6f, 0.8f)]
    private void LoadFromFirstClip()
    {
        if (targetClips.Count == 0 || targetClips[0] == null)
        {
            EditorUtility.DisplayDialog("提示", "目标列表为空或首个动画为空！", "确定");
            return;
        }

        curveTemplates.Clear();
        var clip = targetClips[0];
        var bindings = AnimationUtility.GetCurveBindings(clip);

        foreach (var binding in bindings)
        {
            if (IsExcludedCurve(binding)) continue;
            
            var realCurve = AnimationUtility.GetEditorCurve(clip, binding);
            if (realCurve == null) continue;

            // 将基于秒的真实曲线，压缩回 0~1 的百分比模板曲线
            AnimationCurve normalizedCurve = new AnimationCurve();
            foreach (var key in realCurve.keys)
            {
                float normalizedTime = clip.length > 0 ? key.time / clip.length : 0f;
                normalizedCurve.AddKey(new Keyframe(normalizedTime, key.value, key.inTangent, key.outTangent, key.inWeight, key.outWeight) { weightedMode = key.weightedMode });
            }

            curveTemplates.Add(new BatchCurveEntry
            {
                propertyName = binding.propertyName,
                curve = normalizedCurve
            });
        }
        EditorUtility.DisplayDialog("成功", $"已从 {clip.name} 提取 {curveTemplates.Count} 条曲线", "确定");
    }

    // ==========================================
    // 执行区
    // ==========================================
    [BoxGroup("3. 批量执行")]
    [Button(" 清空列表中的曲线 ", ButtonSizes.Large), GUIColor(0.9f, 0.4f, 0.4f)]
    private void ClearListedCurvesFromClips()
    {
        if (targetClips.Count == 0 || curveTemplates.Count == 0)
        {
            EditorUtility.DisplayDialog("警告", "没有选中动画或没有配置曲线！", "确定");
            return;
        }

        int successCount = 0;

        foreach (var clip in targetClips)
        {
            if (clip == null) continue;

            var bindings = AnimationUtility.GetCurveBindings(clip);

            foreach (var template in curveTemplates)
            {
                string curveName = NormalizeCurveName(template.propertyName);

                foreach (var binding in bindings)
                {
                    if (binding.propertyName == curveName && binding.type == typeof(Animator))
                    {
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                    }
                }
            }

            EditorUtility.SetDirty(clip);
            successCount++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("完成", $"已从 {successCount} 个动画中删除指定曲线。", "确定");
    }
    [BoxGroup("4. Animator参数同步")]
    [LabelText("目标AnimatorController")]
    public AnimatorController targetController;
    [BoxGroup("5. 批量执行")]
    [Button(" 批量烙印到所有动画 ", ButtonSizes.Gigantic), GUIColor(0.2f, 0.9f, 0.2f)]
    private void ApplyBatchToAll()
    {
        if (targetController != null)
        {
            EnsureAnimatorParameters(targetController);
        }
        if (targetClips.Count == 0 || curveTemplates.Count == 0)
        {
            EditorUtility.DisplayDialog("警告", "没有选中动画或没有配置曲线！", "确定");
            return;
        }

        int successCount = 0;

        foreach (var clip in targetClips)
        {
            if (clip == null) continue;

            var existingBindings = AnimationUtility.GetCurveBindings(clip);
            HashSet<string> appliedProperties = new HashSet<string>();

            // 1. 下发模板曲线
            foreach (var template in curveTemplates)
            {
                string curveName = NormalizeCurveName(template.propertyName);
                appliedProperties.Add(curveName);

                // 根据动画的真实长度，把 0~1 的曲线拉伸成 0~Seconds
                AnimationCurve realCurve = new AnimationCurve();
                foreach (var key in template.curve.keys)
                {
                    float realTime = key.time * clip.length;
                    realCurve.AddKey(new Keyframe(realTime, key.value, key.inTangent, key.outTangent, key.inWeight, key.outWeight) { weightedMode = key.weightedMode });
                }

                realCurve.preWrapMode = WrapMode.Clamp;
                realCurve.postWrapMode = WrapMode.Clamp;

                EditorCurveBinding binding = new EditorCurveBinding
                {
                    path = "",
                    propertyName =curveName,
                    type = typeof(Animator)
                };

                AnimationUtility.SetEditorCurve(clip, binding, realCurve);
            }
            
            EditorUtility.SetDirty(clip);
            successCount++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("大功告成", $"批量操作成功！\n已为 {successCount} 个动画写入曲线。", "确定");
    }
    [BoxGroup("6. 代码生成")]
    [Button("生成 Animator Hash 常量", ButtonSizes.Large), GUIColor(0.4f, 0.9f, 0.4f)]
    private void GenerateAnimatorHashes()
    {
        GenerateAnimatorParameterScript();
    }

    
    private string NormalizeCurveName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        if (!name.StartsWith("Curve_"))
            return "Curve_" + name;

        return name;
    }
    private bool IsExcludedCurve(EditorCurveBinding binding)
    {
        if (string.IsNullOrEmpty(binding.path) && binding.path.Contains("Root")) return true;

    string prop = binding.propertyName;
        if (string.IsNullOrEmpty(prop)) return true;
        return prop.StartsWith("RootT.") || prop.StartsWith("RootQ.") || prop.StartsWith("MotionT.") || prop.StartsWith("MotionQ.")||prop.Contains("Local")||prop.Contains("local");
    }
    //自动加属性
    private void EnsureAnimatorParameters(AnimatorController controller)
    {
        if (controller == null) return;

        HashSet<string> existing = new HashSet<string>();

        foreach (var p in controller.parameters)
            existing.Add(p.name);

        foreach (var template in curveTemplates)
        {
            string name = NormalizeCurveName(template.propertyName);

            if (!existing.Contains(name))
            {
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
                Debug.Log($"自动创建 Animator 参数: {name}");
            }
        }

        EditorUtility.SetDirty(controller);
    }
    //自动注入hsah
    private void GenerateAnimatorParameterScript()
    {
        string scriptPath = "Assets/Scripts/App/Comm/Model/CharacterAnimatorParamters.cs";

        HashSet<string> names = new HashSet<string>();

        foreach (var template in curveTemplates)
        {
            if (string.IsNullOrEmpty(template.propertyName))
                continue;

            names.Add(template.propertyName);
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        sb.AppendLine("using UnityEngine;");
        sb.AppendLine("");
        sb.AppendLine("public static class CharacterAnimatorParamters");
        sb.AppendLine("{");

        foreach (var name in names)
        {
            string curveName = NormalizeCurveName(name);

            sb.AppendLine(
                $"    public static readonly int {name} = Animator.StringToHash(\"{curveName}\");"
            );
        }

        sb.AppendLine("}");

        System.IO.File.WriteAllText(scriptPath, sb.ToString());

        AssetDatabase.Refresh();

        Debug.Log($"Animator 参数脚本已生成: {scriptPath}");
    }

    // ==========================================
    // 曲线数据项定义
    // ==========================================
    [Serializable]
    public class BatchCurveEntry
    {[HorizontalGroup("Header", Width = 0.4f)]
        [LabelText("曲线名"), LabelWidth(50)]
        public string propertyName;

        [HorizontalGroup("Header")]
        [LabelText("进度(%)"), LabelWidth(50)][PropertyRange(0f, 100f)]
        public float inputPercent = 0f;

        [HorizontalGroup("Header")]
        [LabelText("值"), LabelWidth(20)]
        public float inputValue = 0f;

        [HorizontalGroup("Header")][Button("快捷打帧"), GUIColor(0.2f, 0.9f, 0.2f)]
        private void AddKeyframe()
        {
            float normalizedTime = inputPercent / 100f;
            Keyframe[] keys = curve.keys;
            bool updated = false;

            for (int i = 0; i < keys.Length; i++)
            {
                if (Mathf.Abs(keys[i].time - normalizedTime) < 0.001f)
                {
                    keys[i].value = inputValue;
                    curve.MoveKey(i, keys[i]);
                    updated = true;
                    break;
                }
            }

            if (!updated)
            {
                curve.AddKey(new Keyframe(normalizedTime, inputValue));
            }
        }
        [BoxGroup("曲线编辑器 (横坐标 0~1)")]
        [HideLabel]
        public AnimationCurve curve = new AnimationCurve();
     
    }
}