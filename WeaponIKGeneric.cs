using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RootMotion.FinalIK;
using Sirenix.OdinInspector;
using UnityEngine.Serialization;

public class WeaponIKGeneric : MonoBehaviour
{
   // --- Final IK 设置 ---
    [FormerlySerializedAs("_aimIK")]
    [Space(10)]
    [Header("AimIK设置")]
    [LabelText("左手 AimIK")] public AimIK leftAimIK;
    [LabelText("右手 AimIK")] public AimIK rightAimIK;
    [LabelText("瞄准平滑时间")] public float aimSmoothTime = 6f;
    [LabelText("左手基础偏移")] public Vector3 leftAimOffset = new Vector3(0, 0.1f, 0); 
    [LabelText("右手基础偏移")] public Vector3 rightAimOffset = new Vector3(0, 0.1f, 0); 
    [LabelText("启用动态Y轴后坐力")] public bool enableDynamicRecoil = true;
    [LabelText("表现总强度 (越大后坐力/起伏越猛)")][Range(0f, 10f)] public float dynamicIntensity = 1f;// 0 就是完全关掉表现，1 是默认，大于1是放大
    [FoldoutGroup("底层参数")][LabelText("基准后坐力系数")] public float baseRecoilMultiplier = 2.5f;
    [FoldoutGroup("底层参数")][LabelText("防跳变阈值")]  public float baseLerpSpeed  = 3f; //越大晃动越小，

    private float snapThreshold = 0.2f; // 防跳变阈值，一帧内位移超过20厘米视为异常跳变
    private float _leftTargetWeight;
    private float _leftCurrentWeight;
    private float _rightTargetWeight;
    private float _rightCurrentWeight;
    private bool _isLeftAttacking;
    private bool _isRightAttacking;
    private bool _needSnapLeft;
    private bool _needSnapRight;
    public bool _isLockedOn;
    // 缓存在面板里拖好的 Target，防止 FinalIK 内部覆盖我们的偏移量
    [ShowInInspector][ReadOnly]private Transform _leftAimTarget;
    [ShowInInspector][ReadOnly]private Transform _rightAimTarget;
    private float _leftHandBaseY;
    private float _rightHandBaseY;
    private float _leftDynamicY;  
    private float _rightDynamicY;
   
    [Space(10)] 
    [Header("骨骼相关")] 
   
    [LabelText("左手骨骼")] public Transform leftHandTransform;
    [LabelText("左小臂骨骼")] public Transform leftHintTransform;
    [LabelText("左大臂骨骼")] public Transform leftUpperArmTransform;
    [LabelText("左臂Bone骨骼_武器")] public Transform leftForeArmBone;
    [Space(5)] 
    [LabelText("右手骨骼")] public Transform rightHandTransform;
    [LabelText("右小臂骨骼")] public Transform rightHintTransform;
    [LabelText("右大臂骨骼")] public Transform rightUpperArmTransform;
    [LabelText("右臂Bone骨骼_武器")] public Transform rightForeArmBone;
    [LabelText("脊柱骨骼")] public Transform spineTransform;
    [LabelText("根骨骼")] public Transform rootTransform;
    private bool _leftHasBone;
    private bool _rightHasBone;

    [Space(25)] 
    [Header("机械臂相关")] 
    [LabelText("胯部骨骼")] public Transform pelvisTransform;
    [LabelText("左机械臂挂点")] public Transform leftMachineArmEquimentPoint;
    [LabelText("右机械臂挂点")] public Transform rightMachineArmEquimentPoint;
    [LabelText("IK目标位置")] public Vector3 machineArmPosition = new Vector3(0.625f, 0.3f, 0.2f);
    [LabelText("插值速度")] public float slerpSpeed = 75f;
    [LabelText("动画误差大小")] public float angelTolerance = 30.0f;
    private bool _leftHasMachineArm = false;
    private bool _rightHasMachineArm = false;
    private List<Transform> _leftMachineArmBones;
    private List<Transform> _rightMachineArmBones;
    
    #region 周期函数
    void OnEnable()
    {
	    _needSnapLeft = true;
	    _needSnapRight = true;
    }
    void Start()
    {
	    _isLockedOn = false;
	    if (leftHandTransform == null || rightHandTransform == null || leftHintTransform == null ||
	        rightHintTransform == null || leftUpperArmTransform == null || rightUpperArmTransform == null)
	    {
		    AutoFindBones();
		    Debug.LogError("WeaponIK [Generic]: 必须手动赋值所有手臂骨骼 (Hand, Hint/ForeArm, UpperArm)！");
	    }
        if (leftAimIK != null) 
        {
	        _leftAimTarget = leftAimIK.solver.target; 
	        leftAimIK.solver.target = null; 
	        if (leftForeArmBone!=null)
	        {
		        leftAimIK.solver.transform = leftForeArmBone;
	        }
	        else
	        {
		        Debug.LogError($"左臂Bone骨骼_武器为空，请先初始化");
	        }
	        leftAimIK.enabled = false;
	     
	      
        }
        if (rightAimIK != null) 
        {
	        _rightAimTarget = rightAimIK.solver.target;
	        rightAimIK.solver.target = null;
	        if (rightForeArmBone!=null)
	        {
		        rightAimIK.solver.transform = rightForeArmBone;
	        }
	        else
	        {
		        Debug.LogError($"右臂Bone骨骼_武器为空，请先初始化");
	        }
	        rightAimIK.enabled = false;
        }
        Transform refRoot = spineTransform;
        if (leftHandTransform != null)
	        _leftHandBaseY = refRoot.InverseTransformPoint(leftHandTransform.position).y;
        if (rightHandTransform != null)
	        _rightHandBaseY = refRoot.InverseTransformPoint(rightHandTransform.position).y;
        
        // 如果设置了武器骨骼，那么用武器骨骼进行计算
        _leftHasBone = leftForeArmBone != null;
        _rightHasBone = rightForeArmBone != null;
        //InitMachineArms();
    }
    private void LateUpdate()
    {
	    // 提取原动画的动态 Y 轴后坐力位移
	    _leftDynamicY = 0f;
	    _rightDynamicY = 0f;
	    if (enableDynamicRecoil)
	    {
		    // 强度为0时，强制不生效；否则强度越大，系数越大
		    float currentRecoil = baseRecoilMultiplier * dynamicIntensity;
            
		    // 限制最小强度防止除以0。强度越大，追赶越慢，晃动越剧烈！
		    float currentLerpSpeed = baseLerpSpeed / Mathf.Max(dynamicIntensity, 0.001f);

		    Transform refRoot = _isLockedOn ? rootTransform : spineTransform;// Spine坐标不一致
		    // ================== 左手逻辑 ==================
		    if (leftHandTransform != null && _leftTargetWeight > 0.01f)
		    {
			    float currentLeftY = refRoot.InverseTransformPoint(leftHandTransform.position).y;
			    // 如果是刚刚换完武器的第一帧，瞬间对齐，绝不产生跳变插值
			    if (_needSnapLeft||Mathf.Abs(currentLeftY - _leftHandBaseY) > snapThreshold)
			    {
				    _leftHandBaseY = currentLeftY;
				    _needSnapLeft = false; // 用完即抛
			    }
			   else if (!_isLeftAttacking ) 
			    {
				    _leftHandBaseY = Mathf.Lerp(_leftHandBaseY, currentLeftY, Time.deltaTime * currentLerpSpeed);
			    }
			    // 计算目标偏移量
			    float yDifferenceL = _isLockedOn ? (currentLeftY - _leftHandBaseY) : (_leftHandBaseY - currentLeftY);
			    _leftDynamicY = yDifferenceL * currentRecoil;//后作力高度
			    //_leftDynamicY = Mathf.Lerp(_leftDynamicY, targetLeftY, Time.deltaTime * 20f);
		    }
		    else
		    {
			    _leftTargetWeight=0;
			    _leftDynamicY = Mathf.Lerp(_leftDynamicY,0f, Time.deltaTime * 20f);
		    }
		    // ================== 右手逻辑 ==================
		    if (rightHandTransform != null && _rightTargetWeight > 0.01f)
		    {
			    float currentRightY = refRoot.InverseTransformPoint(rightHandTransform.position).y;
			    if (_needSnapRight||Mathf.Abs(currentRightY - _rightHandBaseY) > snapThreshold)
			    {
				    _rightHandBaseY = currentRightY;
				    _needSnapRight = false;
			    }
				else if (!_isRightAttacking )
			    {
				    _rightHandBaseY = Mathf.Lerp(_rightHandBaseY, currentRightY, Time.deltaTime * currentLerpSpeed);
			    }
			    float yDifferenceR = _isLockedOn ? (currentRightY - _rightHandBaseY) : (_rightHandBaseY - currentRightY);
			    _rightDynamicY = yDifferenceR* currentRecoil;//后作力高度
			    //_rightDynamicY = Mathf.Lerp(_rightDynamicY, targetRightY, Time.deltaTime * 20f);
			   
		    }
		    else
		    {
			    _rightTargetWeight = 0;
			    _rightDynamicY = Mathf.Lerp(_rightDynamicY, 0f, Time.deltaTime * 20f);
		    }
	    }
	    else
	    {
		    _leftDynamicY = 0f;
		    _rightDynamicY = 0f;
	    }
	    
	    //左手 IK 平滑过渡与更新
	    _leftCurrentWeight = Mathf.Lerp(_leftCurrentWeight, _leftTargetWeight, Time.deltaTime * aimSmoothTime);
	    if (leftAimIK != null && _leftCurrentWeight > 0.001f)
	    {
		    leftAimIK.solver.IKPositionWeight = _leftCurrentWeight;
		    // 获取目标基础位置（如果有Target就用Target的，没有就保持原来的IKPosition）
		    Vector3 basePos = _leftAimTarget != null ? _leftAimTarget.position : leftAimIK.solver.IKPosition;
		    Vector3 finalOffset = leftAimOffset + new Vector3(0, _leftDynamicY, 0);
		    leftAimIK.solver.IKPosition = basePos + finalOffset;
            
		    leftAimIK.solver.Update();
	    }
	    // 右手 IK 平滑过渡与更新
	    _rightCurrentWeight = Mathf.Lerp(_rightCurrentWeight, _rightTargetWeight, Time.deltaTime * aimSmoothTime);
	    if (rightAimIK != null && _rightCurrentWeight > 0.001f)
	    {
		    rightAimIK.solver.IKPositionWeight = _rightCurrentWeight;
            
		    Vector3 basePos = _rightAimTarget != null ? _rightAimTarget.position : rightAimIK.solver.IKPosition;
		    Vector3 finalOffset = rightAimOffset + new Vector3(0, _rightDynamicY, 0);
		    rightAimIK.solver.IKPosition = basePos + finalOffset;
            
		    rightAimIK.solver.Update(); 
	    }
        // 机械臂逻辑
        //UpdateMachineArms();
        // if (_leftTargetWeight == 0 && _rightTargetWeight == 0 && 
        //     _leftCurrentWeight <= 0.01f && _rightCurrentWeight <= 0.01f)
        // {
	       //  _leftCurrentWeight = 0;
	       //  _rightCurrentWeight = 0;
	       //  this.enabled = false;
        // }
    }
    #endregion

    #region 内部逻辑
    private void InitMachineArms()
    {
	    _leftHasMachineArm = TryInitMachineArmChain(leftMachineArmEquimentPoint, "Bone1_main10", out _leftMachineArmBones);
	    _rightHasMachineArm = TryInitMachineArmChain(rightMachineArmEquimentPoint, "Bone1_main11", out _rightMachineArmBones);

	    if ((_leftHasMachineArm || _rightHasMachineArm) && pelvisTransform == null)
	    {
		    Debug.LogError("WeaponIK: 请分配 pelvisTransform，否则无法使用机械臂IK");
		    _leftHasMachineArm = _rightHasMachineArm = false;
	    }
    }
    private bool TryInitMachineArmChain(Transform equipmentPoint, string rootKeyword, out List<Transform> bonesList)
    {
	    bonesList = new List<Transform>();
	    if (equipmentPoint == null) return false;

	    Transform[] children = equipmentPoint.GetComponentsInChildren<Transform>();
	    foreach (Transform child in children)
	    {
		    if (child.name.Contains(rootKeyword))
		    {
			    bonesList.Add(child);
			    break;
		    }
	    }

	    if (bonesList.Count > 0)
	    {
		    Transform current = bonesList[0];
		    int childCount = current.childCount;
		    while (childCount > 0)
		    {
			    for (int i = 0; i < childCount; i++)
			    {
				    Transform child = current.GetChild(i);
				    if (!child.name.Contains("Bone"))
				    {
					    if (i == childCount - 1) childCount = 0;
					    continue;
				    }
                    
				    current = child;
				    childCount = current.childCount;
				    bonesList.Add(current);
				    break;
			    }
		    }
		    return true;
	    }
	    return false;
    }  
    private void UpdateMachineArms()
    {
        if (_leftHasMachineArm)
        {
            Vector3 delta = transform.forward * machineArmPosition.z + transform.up * machineArmPosition.y + (-transform.right * machineArmPosition.x);
            MachineBonesIKSolver(_leftMachineArmBones, pelvisTransform.position + delta, true);
        }
        if (_rightHasMachineArm)
        {
            Vector3 delta = transform.forward * machineArmPosition.z + transform.up * machineArmPosition.y + (transform.right * machineArmPosition.x);
            MachineBonesIKSolver(_rightMachineArmBones, pelvisTransform.position + delta, false);
        }
    }
    private Quaternion[] _leftLastRawRotations;
    private Quaternion[] _rightLastRawRotations;
    private Quaternion[] _leftLastFinalRotations;
    private Quaternion[] _rightLastFinalRotations;
    private void MachineBonesIKSolver(List<Transform> bones, Vector3 targetPosition, bool isLeft)
    {
        int rootIndex = 0;
        int startIndex = 1;
        int secondIndex = 2;
        int endIndex = bones.Count - 1;
        Transform root = bones[rootIndex];
        Transform start = bones[startIndex];
        Transform second = bones[secondIndex];
        Transform end = bones[endIndex];
        
        Quaternion[] lastRawRotations = isLeft ? _leftLastRawRotations : _rightLastRawRotations;
        Quaternion[] lastFinalRotations = isLeft ? _leftLastFinalRotations : _rightLastFinalRotations;
        if (lastRawRotations == null) lastRawRotations = new[] { root.localRotation, start.localRotation, second.localRotation, end.localRotation };
        if (lastFinalRotations == null) lastFinalRotations = new[] { root.localRotation, start.localRotation, second.localRotation, end.localRotation };
        
        Quaternion rootVariation = root.localRotation * Quaternion.Inverse(lastRawRotations[rootIndex]);
        Quaternion startVariation = start.localRotation * Quaternion.Inverse(lastRawRotations[startIndex]);
        Quaternion secondVariation = second.localRotation * Quaternion.Inverse(lastRawRotations[secondIndex]);
        Quaternion endVariation = end.localRotation * Quaternion.Inverse(lastRawRotations[lastRawRotations.Length - 1]);
        if (isLeft)
        {
            if (_leftLastRawRotations == null) _leftLastRawRotations = new Quaternion[4];
            _leftLastRawRotations[rootIndex] = root.localRotation;
            _leftLastRawRotations[startIndex] = start.localRotation;
            _leftLastRawRotations[secondIndex] = second.localRotation;
            _leftLastRawRotations[lastRawRotations.Length - 1] = end.localRotation;
        }
        else
        {
            if (_rightLastRawRotations == null) _rightLastRawRotations = new Quaternion[4];
            _rightLastRawRotations[rootIndex] = root.localRotation;
            _rightLastRawRotations[startIndex] = start.localRotation;
            _rightLastRawRotations[secondIndex] = second.localRotation;
            _rightLastRawRotations[lastRawRotations.Length - 1] = end.localRotation;
        }
        root.localRotation = lastFinalRotations[rootIndex];
        start.localRotation = lastFinalRotations[startIndex];
        second.localRotation = lastFinalRotations[secondIndex];
        end.localRotation = lastFinalRotations[lastFinalRotations.Length - 1];
        
        float angleVariation = startVariation.eulerAngles.x + startVariation.eulerAngles.y + startVariation.eulerAngles.z
                               + secondVariation.eulerAngles.x + secondVariation.eulerAngles.y + secondVariation.eulerAngles.z;
        bool needIK = angleVariation < angelTolerance;

        if (needIK)
        {
            Vector3 secondToStart = start.position - second.position;
            Vector3 secondToEnd = end.position - second.position;
            Vector3 axis = Vector3.Cross(secondToStart, secondToEnd).normalized;
            Vector3 startToEnd = end.position - start.position;
            Vector3 startToTarget = targetPosition - start.position;
            float secondToStartDistance = secondToStart.magnitude;
            float secondToEndDistance = secondToEnd.magnitude;
            float startToTargetDistance = Mathf.Clamp(startToTarget.magnitude, Mathf.Abs(secondToStartDistance - secondToEndDistance) + 0.001f, secondToStartDistance + secondToEndDistance - 0.001f); 
            float theta = Mathf.Acos(Mathf.Clamp(Vector3.Dot(secondToStart.normalized, secondToEnd.normalized), -1.0f, 1.0f));
            float targetTheta = Mathf.Acos(Mathf.Clamp((secondToStartDistance * secondToStartDistance +
                secondToEndDistance * secondToEndDistance - startToTargetDistance * startToTargetDistance) / (2f * secondToStartDistance * secondToEndDistance), -1.0f, 1.0f));
            Quaternion delta = Quaternion.AngleAxis(targetTheta - theta, axis);
            Quaternion targetSecondRotation = delta * second.rotation;
            theta = Mathf.Acos(Mathf.Clamp(Vector3.Dot(-secondToStart.normalized, startToEnd.normalized), -1.0f, 1.0f));
            targetTheta = Mathf.Acos(Mathf.Clamp((secondToStartDistance * secondToStartDistance + 
                startToTargetDistance * startToTargetDistance - secondToEndDistance * secondToEndDistance) / (2f * secondToStartDistance * startToTargetDistance), -1.0f, 1.0f));
            delta = Quaternion.AngleAxis(targetTheta - theta, axis);
            Quaternion targetStartRotation = delta * start.rotation;
            targetStartRotation = Quaternion.FromToRotation((end.position - start.position).normalized, startToTarget.normalized) * targetStartRotation;
            start.rotation = Quaternion.Slerp(start.rotation, targetStartRotation, Time.deltaTime * slerpSpeed);
            second.rotation = Quaternion.Slerp(second.rotation, targetSecondRotation, Time.deltaTime * slerpSpeed);
        }
        
        Quaternion forward = Quaternion.LookRotation(transform.right, transform.up);
        Quaternion deltaToForward = forward * Quaternion.Inverse(end.rotation);
        Quaternion targetRootRotation = deltaToForward * root.rotation;
        root.rotation = targetRootRotation;
        
        root.localRotation = rootVariation * root.localRotation;
        start.localRotation = startVariation * start.localRotation;
        second.localRotation = secondVariation * second.localRotation;
        end.localRotation = endVariation * end.localRotation;

        if (isLeft)
        {
            if (_leftLastFinalRotations == null) _leftLastFinalRotations = new Quaternion[4];
            _leftLastFinalRotations[rootIndex] = root.localRotation;
            _leftLastFinalRotations[startIndex] = start.localRotation;
            _leftLastFinalRotations[secondIndex] = second.localRotation;
            _leftLastFinalRotations[lastFinalRotations.Length - 1] = end.localRotation;
        }
        else
        {
            if (_rightLastFinalRotations == null) _rightLastFinalRotations = new Quaternion[4];
            _rightLastFinalRotations[rootIndex] = root.localRotation;
            _rightLastFinalRotations[startIndex] = start.localRotation;
            _rightLastFinalRotations[secondIndex] = second.localRotation;
            _rightLastFinalRotations[lastFinalRotations.Length - 1] = end.localRotation;
        }
    }
    private void CheckAndLog(string partName, Transform t)
    {
        if (t == null) Debug.LogError($"未能自动找到: {partName} (请检查名字是否匹配)");
        else Debug.Log($"已绑定: {partName} -> {t.name}");
    }
    private void FindAndAssignBonesInternal()
    {
        // 从自身开始递归查找
        Transform[] allChildren = GetComponentsInChildren<Transform>(true);
        
        Dictionary<string, Transform> boneMap = new Dictionary<string, Transform>();
        foreach (var t in allChildren)
        {
            if (!boneMap.ContainsKey(t.name)) boneMap.Add(t.name, t);
        }
        
        if (boneMap.TryGetValue("Bone_R_Forearm", out Transform rBone)) rightForeArmBone = rBone;
        if (boneMap.TryGetValue("Bone_L_Forearm", out Transform lBone)) leftForeArmBone= lBone;
    }
    private void SetSpineWeight(AimIK aimIK, float targetWeight)
    {
	    if (aimIK == null || aimIK.solver == null || aimIK.solver.bones == null) return;

	    foreach (var bone in aimIK.solver.bones)
	    {
		    // 通过名字判断是不是脊柱骨骼（包含 "Spine" 字眼的骨骼）
		    if (bone.transform != null && bone.transform.name.Contains("Spine"))
		    {
			    bone.weight = targetWeight;
		    }
	    }
    }
    #endregion
    
    #region 公开接口
    /// <summary>
    /// 更新武器装备状态。外部检测到玩家武器变化时调用此方法。
    /// </summary>
    /// <param name="hasLeftWeapon">左手是否有武器</param>
    /// <param name="hasRightWeapon">右手是否有武器</param>
    public void UpdateWeaponEquipState(bool hasLeftWeapon, bool hasRightWeapon)
    {
	    _leftTargetWeight = hasLeftWeapon ? 1f : 0f;
	    _rightTargetWeight = hasRightWeapon ? 1f : 0f;

	    // 只要任意手有武器，就唤醒脚本开始运作
	    if (hasLeftWeapon || hasRightWeapon) this.enabled = true;
	    // 既然换了武器，新武器拿在手里的待机高度肯定变了。
	    _needSnapLeft = true;
	    _needSnapRight = true;
	    bool isDualWielding = hasLeftWeapon && hasRightWeapon;
	    float leftSpineWeight = isDualWielding ? 0f : (hasLeftWeapon ? 0.0f : 0f);
	    float rightSpineWeight = isDualWielding ? 0f : (hasRightWeapon ? 0.0f : 0f);
	    
	    SetSpineWeight(leftAimIK, leftSpineWeight);
	    SetSpineWeight(rightAimIK, rightSpineWeight);
    }
    public void UpdateAttackState( bool isAttackLeft, bool isAttackRight)
    {
	    // 缓存攻击状态，供后坐力基准线逻辑使用
	    _isLeftAttacking = isAttackLeft;
	    _isRightAttacking = isAttackRight;
    }
    /// <summary>
    /// 强制立刻停止所有瞄准IK，不带平滑过渡
    /// </summary>
    public void ForceStopAim()
    {
	    _leftTargetWeight = 0f;
	    _rightTargetWeight = 0f;
	    _leftCurrentWeight = 0f;
	    _rightCurrentWeight = 0f;
	    this.enabled = false;
    }
    /// <summary>
    /// 动态修改左手目标偏移量
    /// </summary>
    public void SetLeftAimOffset(Vector3 newOffset)
    {
	    leftAimOffset = newOffset;
    }

    /// <summary>
    /// 动态修改右手目标偏移量
    /// </summary>
    public void SetRightAimOffset(Vector3 newOffset)
    {
	    rightAimOffset = newOffset;
    }

    /// <summary>
    /// 同时修改双手偏移量
    /// </summary>
    public void SetAimOffsets(Vector3 leftOffset, Vector3 rightOffset)
    {
	    leftAimOffset = leftOffset;
	    rightAimOffset = rightOffset;
    }
    /// <summary>
    /// 更新坐标系
    /// </summary>
    public void UpdateLockOnState(bool isLockedOn)
    {
	    // 只有在状态发生【切换】的那一瞬间，才触发对齐
	    if (_isLockedOn != isLockedOn)
	    {
		    _isLockedOn = isLockedOn;
		     // SetSpineWeight(leftAimIK,_isLockedOn?0.5f:0 );
		     // SetSpineWeight(rightAimIK, _isLockedOn?0.5f:0 );
		    // 坐标系变了，旧的基准线彻底作废！,必须强制要求下一帧重新快照 (Snap) 高度，否则武器会瞬间飞天或遁地
		    _needSnapLeft = true;
		    _needSnapRight = true;
	    }
    }
    [ContextMenu("自动绑定骨骼)")]
    public void AutoFindBones()
    {
        Transform[] allChildren = GetComponentsInChildren<Transform>(true);
        foreach (Transform t in allChildren)
        {
	        if (t.name == "Root")
	        {
		        rootTransform = t;
		        break;
	        }
        }
        foreach (Transform t in allChildren)
        {
	        if (t.name == "Bip001 Pelvis")
	        {
		        pelvisTransform = t;
		        break;
	        }
        }
        foreach (Transform t in allChildren)
        {
	        if (t.name == "Bip001 Spine")
	        {
		        spineTransform=t;
		        break;
	        }
        }

        foreach (Transform t in allChildren)
        {
            // --- 右手侧 ---
            if (t.name == "Bip001 R UpperArm") rightUpperArmTransform = t;
            else if (t.name == "Bip001 R Forearm") rightHintTransform = t;
            else if (t.name == "Bip001 R Hand") rightHandTransform = t;
            else if (t.name == "Bone_R_Forearm")
            {
                rightForeArmBone = t;
                _rightHasBone = true; 
            }
            // --- 左手侧 ---
            else if (t.name == "Bip001 L UpperArm") leftUpperArmTransform = t;
            else if (t.name == "Bip001 L Forearm") leftHintTransform = t;
            else if (t.name == "Bip001 L Hand") leftHandTransform = t;
            else if (t.name == "Bone_L_Forearm") 
            {
                leftForeArmBone = t;
                _leftHasBone = true; 
            }
        }
  
   
        CheckAndLog("左大臂", leftUpperArmTransform);
        CheckAndLog("左小臂", leftHintTransform);
        CheckAndLog("左手掌", leftHandTransform);
        CheckAndLog("右大臂", rightUpperArmTransform);
        CheckAndLog("右小臂", rightHintTransform);
        CheckAndLog("右手掌", rightHandTransform);
        
        Debug.Log($"<color=green>骨骼绑定结束！Bone状态: 左[{_leftHasBone}] 右[{_rightHasBone}]</color>");
        
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }
    #endregion
    
    #region 绘制调试
    #if UNITY_EDITOR
    private void OnDrawGizmos()
    {
	    // --- 绘制左手 IK 调试 ---
	    if (leftAimIK != null && leftAimIK.solver != null && leftAimIK.solver.transform != null)
	    {
		    Transform muzzleL = leftAimIK.solver.transform; 
		    Transform targetL = Application.isPlaying ? _leftAimTarget : leftAimIK.solver.target;
            
		    if (targetL != null || leftAimIK.solver.IKPosition != Vector3.zero)
		    {
			    Vector3 basePos = targetL != null ? targetL.position : leftAimIK.solver.IKPosition;
			    // 实时加上左手的动态偏移
			    Vector3 finalPos = basePos + leftAimOffset + new Vector3(0, Application.isPlaying ? _leftDynamicY : 0, 0); 

			    Gizmos.color = Color.green;
			    Gizmos.DrawWireSphere(finalPos, 0.15f);

			    bool hasLeftWeapon = Application.isPlaying ? _leftTargetWeight > 0 : true;
			    Gizmos.color = hasLeftWeapon ? Color.red : Color.gray;
			    Gizmos.DrawLine(muzzleL.position, finalPos);
		    }
	    }

	    // --- 绘制右手 IK 调试 ---
	    if (rightAimIK != null && rightAimIK.solver != null && rightAimIK.solver.transform != null)
	    {
		    Transform muzzleR = rightAimIK.solver.transform; 
		    Transform targetR = Application.isPlaying ? _rightAimTarget : rightAimIK.solver.target;

		    if (targetR != null || rightAimIK.solver.IKPosition != Vector3.zero)
		    {
			    Vector3 basePos = targetR != null ? targetR.position : rightAimIK.solver.IKPosition;
			    Vector3 finalPos = basePos + rightAimOffset + new Vector3(0, Application.isPlaying ? _rightDynamicY : 0, 0);

			    Gizmos.color = Color.green;
			    Gizmos.DrawWireSphere(finalPos, 0.15f);

			    bool hasRightWeapon = Application.isPlaying ? _rightTargetWeight > 0 : true;
			    Gizmos.color = hasRightWeapon ? Color.red : Color.gray;
			    Gizmos.DrawLine(muzzleR.position, finalPos);
		    }
	    }
    }
	#endif
    #endregion
}