using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;

namespace ET.Client
{
    public class RootMotionGenerator : OdinEditorWindow
    {
        [MenuItem("ET/RootMotion 烘焙器")]
        public static void ShowWindow()
        {
            var window = GetWindow<RootMotionGenerator>("RootMotion 烘焙器");
            window.position = new Rect(100, 100, 600, 800);
        }

        public enum BakeMethod
        {
	        [LabelText("引擎驱动(适合Humanoid,规范Generic)")]
            AnimatorEngine,[LabelText("强制原始采样(适合无Avatar的Generic)")]
            RawCurveSampling
        }
        [Title("基础设置", "设置采样规则与目标动画(作用于单体和批量)", TitleAlignments.Left)][Space(10)][LabelText("采样帧率")][PropertyTooltip("每秒采样的次数，建议保持在30-60帧")]
        [Range(5, 60)]
        [OnValueChanged("UpdatePreviewData")]
        public int SampleRate = 30;

        [LabelText("烘焙包含位移")][PropertyTooltip("如果关闭，则只会提取动画的旋转数据，位移强制为0")][OnValueChanged("UpdatePreviewData")]
        public bool BakePosition = true;

        #region 单个动画烘焙
        [Title("单文件烘焙", "选择单个片段进行详细检测与烘焙", TitleAlignments.Left)][Space(10)]
        [LabelText("目标动画片段")]
        [OnValueChanged("OnClipChanged")]
        [InlineButton("OnClipChanged", "重新检测")]
        public AnimationClip SelectedClip;

        [LabelText("提取模式")][PropertyTooltip("遇到Generic动画烘焙出0位移时，请切换为RawCurve模式")][GUIColor(1f, 0.8f, 0.4f)]
        [OnValueChanged("UpdatePreviewData")]
        public BakeMethod ExtractMethod = BakeMethod.AnimatorEngine;

        #region 实时预览数据[Title("RootMotion 预览数据", "在烘焙前实时查看提取结果", TitleAlignments.Left)]
        [Space(10)]
        [LabelText("预期总位移 (米)")]
        [DisplayAsString]
        public string PreviewTotalPositionStr = "0.00, 0.00, 0.00";[LabelText("预期总旋转 (度)")]
        [DisplayAsString]
        public string PreviewTotalRotationStr = "0.00°";
        [HideInInspector]
        public float PreviewTotalRotationAngle;

        [ShowIf("ShouldShowRotationWarning")][InfoBox("该动画没有 Root 旋转，那就说明 Root 没有动，需要改资源！", InfoMessageType.Error)]
        public string RotationWarning = "";

        private bool ShouldShowRotationWarning => SelectedClip != null && PreviewTotalRotationAngle < 0.5f;
        #endregion

        #region 动画曲线检测数据[Title("曲线物理检测", "检测底层 Root 和 Motion 曲线结构", TitleAlignments.Left)][Space(10)]
        [HideInInspector] public bool hasRoot;
        [HideInInspector] public bool isHumanoid;
        [HideInInspector] public bool hasMotion;

        [ShowIf("hasRoot")][InfoBox("动画存在 Root 节点的本地 Transform 位移/旋转数据", InfoMessageType.Info)][ListDrawerSettings(IsReadOnly = true, Expanded = true)]
        [LabelText("Root 曲线列表")]
        public List<string> RootInfos = new List<string>();

        [ShowIf("hasMotion")]
        [ShowIf("isHumanoid")]
        [InfoBox("检测到 Motion 曲线。这是 Humanoid 特有曲线，强烈建议使用[Animator 引擎驱动] 模式提取。", InfoMessageType.Warning)][ListDrawerSettings(IsReadOnly = true, Expanded = true)]
        [LabelText("Motion 曲线列表")]
        public List<string> MotionInfos = new List<string>();

        [HideIf("HasAnyCurveWarning")][InfoBox("检测通过：未发现特殊曲线。", InfoMessageType.None)]
        [LabelText("检测状态")]
        [DisplayAsString]
        public string CheckStatus = "常规动画";

        private bool HasAnyCurveWarning => hasRoot || hasMotion;
        #endregion

        #region 单个执行操作按钮
        [HorizontalGroup("Actions")][Button("烘焙并生成配置", ButtonSizes.Large)][GUIColor(0.4f, 0.8f, 1f)]
        [EnableIf("@this.SelectedClip != null")]
        private void GenerateRootMotionData()
        {
            DoGenerate();
        }[HorizontalGroup("Actions")]
        [Button("导出为二进制", ButtonSizes.Large)][GUIColor(0.4f, 1f, 0.4f)]
        private void ExportToBinary()
        {
            try
            {
                RootMotionScriptableObject.Export();
                EditorUtility.DisplayDialog("成功", "RootMotion 二进制数据导出成功！", "确定");
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("导出失败", e.ToString(), "确定");
            }
        }
        #endregion
        #endregion

        #region 批量烘焙功能[Title("批量烘焙区", "放入动画文件夹，自动向下检测包含 180/90 等语义的动画", TitleAlignments.Left)]
        [Space(10)]
        [FolderPath]
        [LabelText("批量扫描文件夹")]
        [OnValueChanged("OnBatchFolderChanged")]
        public string BatchScanFolder;

        [Serializable]
        public class BatchAnimItem
        {
	        [TableColumnWidth(220, Resizable = false)]
            [LabelText("动画片段")]
            public AnimationClip Clip;

            [TableColumnWidth(160, Resizable = false)]
            [LabelText("提取模式(支持微调)")]
            public BakeMethod Method;
           
            [LabelText("预览总位移")]
            [DisplayAsString]
            public string TotalPositionStr;
           
            [LabelText("预览总旋转")]
            [DisplayAsString]
            public string TotalRotationStr;
        }
        
        [TableList(IsReadOnly = true, AlwaysExpanded = true)]
        [LabelText("待烘焙列表 (语义包含180/90)")]
        public List<BatchAnimItem> BatchList = new List<BatchAnimItem>();

        [Button("重新扫描并检测", ButtonSizes.Medium)][GUIColor(0.6f, 0.9f, 1f)]
        private void OnBatchFolderChanged()
        {
            BatchList.Clear();
            if (string.IsNullOrEmpty(BatchScanFolder) || !Directory.Exists(BatchScanFolder)) return;

            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { BatchScanFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;

                string[] splits = clip.name.Split('_');
                // 动画语义应在按 '_' 分割的数组中（通常是最后一段或第三段），需至少2段
                if (splits.Length < 2) continue;

                // 取最后一段作为动画语义
                string animName = splits[splits.Length - 1];
                
                // 过滤包含 180 或 90 的动画 (带根旋转)
                if (animName.Contains("180") || animName.Contains("90"))
                {
                    // 获取智能推荐的烘焙模式
                    BakeMethod recommendedMethod = DetermineBakeMethod(clip);
                    
                    // 模拟计算预览数据
                    CalculateClipData(clip, recommendedMethod, out Vector3 pos, out Quaternion rot);
                    float rotAngle = Quaternion.Angle(Quaternion.identity, rot);

                    BatchList.Add(new BatchAnimItem
                    {
                        Clip = clip,
                        Method = recommendedMethod,
                        TotalPositionStr = $"{pos.x:F2}, {pos.y:F2}, {pos.z:F2}",
                        TotalRotationStr = $"{rotAngle:F2}°"
                    });
                }
            }
        }

        [HorizontalGroup("BatchActions")][Button("批量烘焙并导出二进制", ButtonSizes.Large)][GUIColor(1f, 0.5f, 0.2f)]
        [EnableIf("@this.BatchList.Count > 0")]
        private void BatchBakeAndExport()
        {
            if (BatchList.Count == 0) return;

            int successCount = 0;
            try
            {
                foreach (var item in BatchList)
                {
                    if (item.Clip == null) continue;
                    // 批量时不单独 Ping，后台静默生成即可
                    bool success = DoGenerateForClip(item.Clip, item.Method, false);
                    if (success) successCount++;
                }

                // 统一保存
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // 统一导出二进制
                RootMotionScriptableObject.Export();
                EditorUtility.DisplayDialog("批量烘焙成功", $"共成功烘焙了 {successCount} 个动画文件，并成功导出二进制！", "确定");
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("批量导出异常", e.ToString(), "确定");
                ET.Log.Error(e.ToString());
            }
        }
        #endregion

        #region 内部逻辑实现

        private void OnClipChanged()
        {
            RootInfos.Clear();
            MotionInfos.Clear();
            hasRoot = false;
            hasMotion = false;

            if (SelectedClip == null)
            {
                UpdatePreviewData();
                return;
            }

            isHumanoid = SelectedClip.humanMotion || SelectedClip.isHumanMotion;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(SelectedClip);
            bool hasRootQorT = false;

            foreach (var binding in bindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(SelectedClip, binding);
                if (curve == null || curve.length == 0) continue;

                float first = curve.keys[0].value;
                float last = curve.keys[curve.length - 1].value;
                bool changed = Mathf.Abs(first - last) > 0.0001f;

                first = Mathf.Round(first * 1000f) / 1000f;
                last = Mathf.Round(last * 1000f) / 1000f;
                string info = $"{binding.propertyName} (有变化: {changed}) | 首帧:{first} | 尾帧:{last}";

                if (string.IsNullOrEmpty(binding.path))
                {
                    if (binding.propertyName.StartsWith("m_LocalPosition") || binding.propertyName.StartsWith("m_LocalRotation") || binding.propertyName.StartsWith("Root"))
                    {
                        hasRoot = true;
                        RootInfos.Add(info);
                        if (binding.propertyName.StartsWith("RootT") || binding.propertyName.StartsWith("RootQ"))
                            hasRootQorT = true;
                    }
                }

                if (binding.propertyName.Contains("MotionT") || binding.propertyName.Contains("MotionQ"))
                {
                    hasMotion = true;
                    MotionInfos.Add(info);
                }
            }

            if (hasMotion || isHumanoid || hasRootQorT)
            {
                ExtractMethod = BakeMethod.AnimatorEngine;
            }
            else if (hasRoot)
            {
                ExtractMethod = BakeMethod.RawCurveSampling;
            }

            UpdatePreviewData();
        }

        private void UpdatePreviewData()
        {
            if (SelectedClip == null)
            {
                PreviewTotalPositionStr = "0.00, 0.00, 0.00";
                PreviewTotalRotationStr = "0.00°";
                PreviewTotalRotationAngle = 0f;
                return;
            }

            CalculateClipData(SelectedClip, ExtractMethod, out Vector3 totalPosAccumulation, out Quaternion totalRotAccumulation);

            PreviewTotalPositionStr = $"{totalPosAccumulation.x:F2}, {totalPosAccumulation.y:F2}, {totalPosAccumulation.z:F2}";
            PreviewTotalRotationAngle = Quaternion.Angle(Quaternion.identity, totalRotAccumulation);
            PreviewTotalRotationStr = $"{PreviewTotalRotationAngle:F2}°";
        }

        private void DoGenerate()
        {
            DoGenerateForClip(SelectedClip, ExtractMethod, true);
        }

        // 抽取独立的生成逻辑，可支持单体与批量循环复用
        private bool DoGenerateForClip(AnimationClip clip, BakeMethod extractMethod, bool isSingleBake)
        {
            string folderPath = "Assets/Config/RootMotion";
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

            string[] splits = clip.name.Split('_');
            if (splits.Length < 2)
            {
                if (isSingleBake) EditorUtility.DisplayDialog("命名错误", $"动画命名不符合规范: {clip.name}", "确定");
                else ET.Log.Error($"动画命名不符合规范: {clip.name}");
                return false;
            }

            Match match = Regex.Match(splits[splits.Length - 2], @"^\d+");
            string idStr = match.Success ? match.Value : string.Empty;
            if (!int.TryParse(idStr, out int characterId))
            {
                ET.Log.Error($"解析角色 ID 失败: {idStr}");
                return false;
            }

            string animName = splits[splits.Length - 1];
            string assetName = $"{idStr}_{animName}";
            string assetPath = Path.Combine(folderPath, assetName + ".asset");

            RootMotionScriptableObject asset = AssetDatabase.LoadAssetAtPath<RootMotionScriptableObject>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<RootMotionScriptableObject>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }
            asset.RootMotionConfig ??= new RootMotionConfig();

            asset.RootMotionConfig.Id = RootMotionIdHelper.GetId(characterId, animName);
            asset.RootMotionConfig.TotalDuration = clip.length;

            BakeKeyframesData(clip, extractMethod, asset.RootMotionConfig);

            EditorUtility.SetDirty(asset);

            if (isSingleBake)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
                Debug.Log($"<color=green>RootMotion 生成成功!</color> 模式: {extractMethod} | 路径: {assetPath}");
            }
            else
            {
                Debug.Log($"批量生成: <color=cyan>{clip.name}</color> -> 路径: {assetPath}");
            }

            return true;
        }

        // 后台辅助：为独立剪辑计算预览参数
        private void CalculateClipData(AnimationClip clip, BakeMethod extractMethod, out Vector3 totalPosAccumulation, out Quaternion totalRotAccumulation)
        {
            totalPosAccumulation = Vector3.zero;
            totalRotAccumulation = Quaternion.identity;

            GameObject tempObject = new GameObject("TempPreviewSampler");
            tempObject.hideFlags = HideFlags.HideAndDontSave;

            int sampleCount = Mathf.CeilToInt(clip.length * SampleRate);
            float timeStep = clip.length / sampleCount;

            if (extractMethod == BakeMethod.AnimatorEngine)
            {
                Animator animator = tempObject.AddComponent<Animator>();
                animator.applyRootMotion = true;
                var controller = new UnityEditor.Animations.AnimatorController();
                controller.AddLayer("Base Layer");
                controller.layers[0].stateMachine.AddState("TempState").motion = clip;
                animator.runtimeAnimatorController = controller;

                animator.Play("TempState", 0, 0f);
                animator.Update(0f);

                Vector3 lastPos = tempObject.transform.position;
                Quaternion lastRot = tempObject.transform.rotation;

                for (int i = 1; i <= sampleCount; i++)
                {
                    animator.Update(timeStep);
                    Vector3 currentPos = tempObject.transform.position;
                    Quaternion currentRot = tempObject.transform.rotation;

                    Vector3 deltaPos = BakePosition ? (currentPos - lastPos) : Vector3.zero;
                    Quaternion deltaRot = Quaternion.Inverse(lastRot) * currentRot;

                    totalPosAccumulation += deltaPos;
                    totalRotAccumulation = totalRotAccumulation * deltaRot;
                    lastPos = currentPos;
                    lastRot = currentRot;
                }
                DestroyImmediate(controller);
            }
            else
            {
                clip.SampleAnimation(tempObject, 0f);
                Vector3 lastPos = tempObject.transform.localPosition;
                Quaternion lastRot = tempObject.transform.localRotation;

                for (int i = 1; i <= sampleCount; i++)
                {
                    clip.SampleAnimation(tempObject, i * timeStep);
                    Vector3 currentPos = tempObject.transform.localPosition;
                    Quaternion currentRot = tempObject.transform.localRotation;

                    Vector3 deltaPos = BakePosition ? (currentPos - lastPos) : Vector3.zero;
                    Quaternion deltaRot = Quaternion.Inverse(lastRot) * currentRot;

                    totalPosAccumulation += deltaPos;
                    totalRotAccumulation = totalRotAccumulation * deltaRot;
                    lastPos = currentPos;
                    lastRot = currentRot;
                }
            }
            DestroyImmediate(tempObject);
        }

        // 后台辅助：智能推断模式 (供批量扫描时初始化预测用)
        private BakeMethod DetermineBakeMethod(AnimationClip clip)
        {
            if (clip == null) return BakeMethod.AnimatorEngine;

            bool human = clip.humanMotion || clip.isHumanMotion;
            bool hRoot = false, hMotion = false, hRootQorT = false;

            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var binding in bindings)
            {
                if (string.IsNullOrEmpty(binding.path))
                {
                    if (binding.propertyName.StartsWith("m_LocalPosition") || binding.propertyName.StartsWith("m_LocalRotation") || binding.propertyName.StartsWith("Root"))
                    {
                        hRoot = true;
                        if (binding.propertyName.StartsWith("RootT") || binding.propertyName.StartsWith("RootQ")) hRootQorT = true;
                    }
                }
                if (binding.propertyName.Contains("MotionT") || binding.propertyName.Contains("MotionQ")) hMotion = true;
            }

            if (hMotion || human || hRootQorT) return BakeMethod.AnimatorEngine;
            if (hRoot) return BakeMethod.RawCurveSampling;

            return BakeMethod.AnimatorEngine;
        }

        private void BakeKeyframesData(AnimationClip clip, BakeMethod extractMethod, RootMotionConfig config)
        {
            config.Keyframes ??= new List<RootMotionConfig.Keyframe>();
            config.Keyframes.Clear();

            GameObject tempObject = new GameObject("TempSampler");
            tempObject.hideFlags = HideFlags.HideAndDontSave;

            int sampleCount = Mathf.CeilToInt(clip.length * SampleRate);
            float timeStep = clip.length / sampleCount;

            if (extractMethod == BakeMethod.AnimatorEngine)
            {
                Animator animator = tempObject.AddComponent<Animator>();
                animator.applyRootMotion = true;

                var controller = new UnityEditor.Animations.AnimatorController();
                controller.AddLayer("Base Layer");
                controller.layers[0].stateMachine.AddState("TempState").motion = clip;
                animator.runtimeAnimatorController = controller;

                animator.Play("TempState", 0, 0f);
                animator.Update(0f);
                Vector3 initialPos = tempObject.transform.position;
                Quaternion initialRot = tempObject.transform.rotation;

                for (int i = 1; i <= sampleCount; i++)
                {
                    float time = i * timeStep;
                    animator.Update(timeStep);

                    Vector3 currentPos = tempObject.transform.position;
                    Quaternion currentRot = tempObject.transform.rotation;

                    RecordValue(time, currentPos, currentRot, initialPos, initialRot, config);
                }
                config.TotalPosition = BakePosition ? (tempObject.transform.position - initialPos) : Vector3.zero;
                config.TotalRotation = Quaternion.Inverse(initialRot) * tempObject.transform.rotation;
                DestroyImmediate(controller);
            }
            else
            {
                clip.SampleAnimation(tempObject, 0f);

                Vector3 initialPos = tempObject.transform.localPosition;
                Quaternion initialRot = tempObject.transform.localRotation;

                for (int i = 1; i <= sampleCount; i++)
                {
                    float time = i * timeStep;
                    clip.SampleAnimation(tempObject, time);

                    Vector3 currentPos = tempObject.transform.localPosition;
                    Quaternion currentRot = tempObject.transform.localRotation;

                    RecordValue(time, currentPos, currentRot, initialPos, initialRot, config);
                }
                config.TotalPosition = BakePosition ? (tempObject.transform.localPosition - initialPos) : Vector3.zero;
                config.TotalRotation = Quaternion.Inverse(initialRot) * tempObject.transform.localRotation;
            }

            DestroyImmediate(tempObject);
        }

        private void RecordValue(float time, Vector3 currentPos, Quaternion currentRot, Vector3 initialPos, Quaternion initialRot, RootMotionConfig config)
        {
            Vector3 offsetPos = currentPos - initialPos;
            Quaternion offsetRot = Quaternion.Inverse(initialRot) * currentRot;

            if (!BakePosition) offsetPos = Vector3.zero;

            config.Keyframes.Add(new RootMotionConfig.Keyframe
            {
                Time = time,
                Value = new QSTransform(offsetPos, offsetRot)
            });
        }

        #endregion
    }
}