using System;
using UnityEngine;

namespace PeakReplayLab;

// CanvasGroup is a native Behaviour rather than a MonoBehaviour. Player-build
// JsonUtility rejects it, so its small visual state must be copied directly.
internal static class NativeUiComponentCopy
{
    public static void Copy(Component source, Component copy)
    {
        if (!source || !copy) throw new ArgumentException("原版视觉控件已被移除。");
        if (source.GetType() != copy.GetType())
            throw new InvalidOperationException("原版视觉控件的复制类型不一致。");
        if (source is CanvasGroup group && copy is CanvasGroup copiedGroup)
        {
            copiedGroup.alpha = group.alpha;
            copiedGroup.interactable = group.interactable;
            copiedGroup.blocksRaycasts = group.blocksRaycasts;
            copiedGroup.ignoreParentGroups = group.ignoreParentGroups;
        }
        else if (source is MonoBehaviour && copy is MonoBehaviour)
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), copy);
        else
            throw new InvalidOperationException("未验证的原生视觉控件复制类型：" + source.GetType().Name);

        if (source is Behaviour original && copy is Behaviour copied) copied.enabled = original.enabled;
    }
}
