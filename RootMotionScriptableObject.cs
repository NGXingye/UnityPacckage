using System;
using System.Collections;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEngine;

namespace ET.Client
{
    [CreateAssetMenu(menuName = "ET/RootMotionScriptableObject")]
    [EnableClass]
    [HideMonoScript]
    public class RootMotionScriptableObject : ScriptableConfigObject<RootMotionScriptableObject, RootMotionConfig, int>
	{
        [Title("RootMotion 配置数据", TitleAlignment = TitleAlignments.Centered)]
        [LabelText("RootMotion数据")]
        [NonSerialized, OdinSerialize]
        [HideLabel]
        [HideReferenceObjectPicker]
        public RootMotionConfig RootMotionConfig = new();

		public override RootMotionConfig Value => RootMotionConfig;
	}
}