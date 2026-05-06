using MemoryPack;
using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using Unity.Mathematics;

namespace ET
{
	public static class RootMotionIdHelper
	{
		[StaticField]
		private static Dictionary<MotionType, string> RootMotionDict = new()
		{
			{ MotionType.Knockback, "knockback" }, 
			{ MotionType.RunToIdleLeft , "runtoidleleft"},
			{ MotionType.RunToIdleRight , "runtoidleright"},
			{ MotionType.TurnLeft180 , "turnleft180"},
			{ MotionType.TurnRight180 , "turnright180"},
			{ MotionType.Attack1 , "attack1"},
			{ MotionType.Attack2, "attack2"},
		};

		[StaticField]
		private static Dictionary<(int, string), int> dict = new();
		
		public static int GetId(int charConfigId, string animName)
		{
			if (string.IsNullOrEmpty(animName)) return 0;
			
			if (dict.TryGetValue((charConfigId, animName), out var id))
			{
				return id;
			}
			
			string uniqueString = $"{charConfigId}_{animName}";
			id = GetStringHash(uniqueString);
			
			dict.Add((charConfigId, animName), id);
        
			return id;
		}

		public static int GetId(int charConfigId, MotionType motionType)
		{
			string animName = GetRootMotionName(motionType);
			return GetId(charConfigId, animName);
		}

		public static string GetRootMotionName(MotionType motionType)
		{
			// return RootMotionDict.GetValueOrDefault(motionType);
			if (!RootMotionDict.TryGetValue(motionType, out var motionName))
			{
				return motionType.ToString().ToLower(); // 以防万一
			}
			return motionName;
		}
		
		private static int GetStringHash(string str)
		{
			unchecked
			{
				int hash = 5381;
				foreach (char c in str)
				{
					hash = ((hash << 5) + hash) + c;
				}
				return hash;
			}
		}
	}
	
	[Serializable]
	[HideReferenceObjectPicker]
	[MemoryPackable]
	public partial class RootMotionConfig : ScriptableConfig<RootMotionConfig, int>
	{
		[Serializable]
		public struct Keyframe
		{
			[LabelText("时间")]
			public float Time;
		
			[LabelText("绝对偏移量")]
			public QSTransform Value; 
		
		}

		/// <summary>
		/// 用于采样动作的游标
		/// </summary>
		public struct Cursor
		{
			public int Index;// 当前所在的关键帧索引
			public float Time; // 当前已经过去的时间

			public bool IsStart => Index == 0 && Time == 0;
			public bool IsValid => Index >= 0 && Time >= 0;

			public static Cursor Start => default;
			public static Cursor Invalid => new Cursor { Index = -1, Time = -1 };
		}
		[ReadOnly]
		[LabelText("总位移")]
		public float3 TotalPosition;
		[ReadOnly]
		[LabelText("总旋转")]
		public quaternion TotalRotation;
		[ReadOnly]
		[LabelText("动画时长（秒）")]
		public float TotalDuration;
		[ReadOnly]
		[LabelText("关键帧")]
		public List<Keyframe> Keyframes = new();

		public bool IsValid => Keyframes != null && Keyframes.Count > 0;

		/// <summary>
		/// Advances the specified cursor forward in time by the given duration, updating its position to the appropriate
		/// keyframe.
		/// </summary>
		/// <remarks>If the advanced time surpasses one or more keyframes, the cursor's index is incremented to the
		/// latest keyframe that does not exceed the new time. This method does not clamp the cursor to the end of the
		/// keyframe list; callers should ensure the cursor remains within valid bounds if necessary.</remarks>
		/// <param name="cursor">The cursor that tracks the current position and time within the keyframe sequence. The cursor is updated to
		/// reflect the new position after advancement.</param>
		/// <param name="dt">The amount of time, in seconds, to advance the cursor.</param>
		/// <returns>The updated cursor reflecting the new time and keyframe index after advancement.</returns>
		public Cursor Advance(Cursor cursor, float dt)
		{
			cursor.Time += dt;
			while (cursor.Index + 1 < Keyframes.Count && Keyframes[cursor.Index + 1].Time <= cursor.Time)
				++cursor.Index;
			return cursor;
		}

		/// <summary>
		/// 判断游标是否达到结尾
		/// </summary>
		/// <param name="cursor"></param>
		/// <returns></returns>
		public bool IsEnd(Cursor cursor)
		{
			return cursor.Index + 1 >= Keyframes.Count;
		}

		/// <summary>
		/// 依据游标进行采样
		/// </summary>
		/// <param name="cursor"></param>
		/// <returns></returns>
		/// <exception cref="IndexOutOfRangeException"></exception>
		 public QSTransform Sample(Cursor cursor)
		{
			if (cursor.Index >= Keyframes.Count)
				throw new IndexOutOfRangeException();
		
			if (Keyframes.Count == 1)
				return Keyframes[0].Value;
		
			var index = cursor.Index;
			if (index + 1 < Keyframes.Count)
			{
				var prevTime = Keyframes[index].Time;
				var nextTime = Keyframes[index + 1].Time;
				var factor = math.saturate((cursor.Time - prevTime) / math.max(nextTime - prevTime, 1e-6f));
				return QSTransform.Lerp(Keyframes[index].Value, Keyframes[index + 1].Value, factor);
			}
			else return Keyframes[index].Value;
		}
	
	}
}