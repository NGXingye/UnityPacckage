using UnityEngine;
using UnityEditor;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using System.IO;


public class AnimationPreviewerEditor : OdinEditorWindow
{
    [MenuItem("通用工具/动画查看面板")]
    private static void OpenWindow()
    {
        var window = GetWindow<AnimationPreviewerEditor>();
        window.titleContent = new GUIContent("动画播放器");
        window.Show();
    }
    
    private const string TargetEditorKeyword = "ActionEditor";
    
    // =======================================================================环境检查逻辑 ===========================
    private bool IsCorrectEditorEnvironment()
    {
        // Application.dataPath 返回的是 ".../Assets",检查全路径是否包含关键词 (忽略大小写)
        return Application.dataPath.IndexOf(TargetEditorKeyword, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [ShowIf("@!this.IsCorrectEditorEnvironment()")]
    [InfoBox("当前编辑器环境非 [ActionEditor]，无权使用此功能！\n请检查工程目录名称或权限配置。", InfoMessageType.Error)]
    [ReadOnly] // 让下面的属性变灰，虽然不显示但占位
    public string WarningPlaceholder = "权限被拒绝";
    //===================================================配置区域======================================================
    [ShowIf("IsCorrectEditorEnvironment")]
    private string parenName = "NetworkSpawn(Clone)";
    private string targetName = "Charecter_1";//挂载的目标
    // 定义搜索动画资源的根目录
    private const string AnimationRootSearchPath = "Assets/Resources/animations/model";
    
    //状态，显示
    [Title("运行时操作")]
    [ShowInInspector, ReadOnly, LabelText("当前状态")]
    private string _statusInfo = "等待操作...";
    [ShowInInspector, ReadOnly, LabelText("目标角色对象")]
    private GameObject _targetCharacter;
    
    [Title("预览器控制")]
    [ShowIf("_previewerInstance")]
    [InlineEditor(InlineEditorObjectFieldModes.Hidden)] 
    public AnimationPreviewer _previewerInstance;
    
    [Button("查找预览器", ButtonSizes.Large), GUIColor(0.4f, 0.8f, 1f)]
    [EnableIf("IsGamePlaying")]
    private void SetupPreviewer()
    {
       
        if (!Application.isPlaying)
        {
            _statusInfo = "先运行游戏";
            return;
        }
        //找对生成角色的游戏对象
        GameObject parentObj = GameObject.Find(parenName);
        if (parentObj == null)
        {
            return;
        }
      
        Transform childTrans = parentObj.transform.Find(targetName);// 使用 transform.Find 可以找到隐藏的或特定层级的子物体
        if (childTrans == null)
        {
            foreach (Transform t in parentObj.transform)
            {
                if (t.name.StartsWith("Character_"))
                {
                    childTrans = t;
                    break;
                }
            }
        }
        _targetCharacter = childTrans.gameObject;
        
        //挂在的脚本
        _previewerInstance = _targetCharacter.GetComponent<AnimationPreviewer>();
        if (_previewerInstance == null)
        {
            _previewerInstance = _targetCharacter.AddComponent<AnimationPreviewer>();
            _statusInfo = "成功：脚本已挂载";
            Debug.Log($"[动作工具] 已成功将 AnimationPreviewer 挂载到 {_targetCharacter.name}");
        }
        else
        {
            _statusInfo = "成功：获取到已有脚本";
        }
        
        //自动根据子对象配置路径
        AutoConfigure(_previewerInstance,childTrans);

        Selection.activeGameObject = _targetCharacter;
    }

    private void AutoConfigure(AnimationPreviewer previewer, Transform rootBone)
    {
        previewer.character = rootBone.gameObject;
        
        string modelName = "";

        Animator anim = rootBone.GetComponent<Animator>();
        
        if (anim != null && anim.avatar != null)
        {
            string avatarName = anim.avatar.name;
            avatarName = avatarName.Replace("(Clone)", "").Trim();

            int idx = avatarName.IndexOf("_avatar");
            if (idx>=0)
            {
                modelName = avatarName.Substring(0, idx);
            }
            else
            {
                Debug.LogWarning($"[动作工具] Avatar 名称不符合规则: {avatarName}");
            }
            
            if (string.IsNullOrEmpty(modelName))
            {
                Debug.LogError($"[动作工具] modelName 解析失败，为空，AvatarName={avatarName}");
            }
            Debug.Log($"[动作工具] 尝试使用 Avatar={avatarName} → modelName={modelName}");
           
        }
        string[] guids = AssetDatabase.FindAssets($"{modelName} t:Folder", new[] { AnimationRootSearchPath }); 
        
        if (guids.Length == 0)
        {
            Debug.LogWarning($"[动作工具] 未能在 {AnimationRootSearchPath} 下找到名为 {modelName} 的文件夹。");
          
            if (rootBone.childCount > 0)
            {
                modelName = rootBone.GetChild(0).name; 
            }
           
            Debug.Log($"[动作工具] 识别到模型名称: {modelName}");
            
            //加载动画路劲
           guids = AssetDatabase.FindAssets($"{modelName} t:Folder", new[] { AnimationRootSearchPath });
           
        }
        
        if (guids.Length > 0)
        {
            // 找到路径 (AssetDatabase 返回的是 GUID，需要转换)
            string folderPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            
            // 赋值给脚本
            previewer.animationsPath = folderPath;
            
            _statusInfo = $"配置成功：已加载 {modelName}";
            Debug.Log($"[动作工具] 路径自动匹配成功: {folderPath}");
            //刷新下动画
            previewer.LoadAnimations(); 
        }
        else
        {
            _statusInfo = $"警告：未找到路径，请手动配置";
            Debug.LogError($"[动作工具] 彻底查找失败，请检查资源目录下是否存在名为 {modelName} 的文件夹");
        }
    }

    //=================================================================================================================
    [Button("卸载预览器", ButtonSizes.Medium), GUIColor(1f, 0.5f, 0.5f)]
    [ShowIf("_previewerInstance")]
    private void RemovePreviewer()
    {
        if (_previewerInstance != null)
        {
            DestroyImmediate(_previewerInstance);
            _previewerInstance = null;
            _statusInfo = "已卸载组件";
        }
    }
    private bool IsGamePlaying => Application.isPlaying;
    private void OnSelectionChange()
    {
        if (_targetCharacter == null && _previewerInstance != null)
        {
            _previewerInstance = null;
        }
    }
    // 窗口重绘，保持状态更新
    private void OnInspectorUpdate()
    {
        if(Application.isPlaying && _targetCharacter == null && _previewerInstance != null)
        {
            // 处理停止运行后的清理
            _previewerInstance = null;
            _statusInfo = "游戏已停止";
            Repaint();
        }
    }
}