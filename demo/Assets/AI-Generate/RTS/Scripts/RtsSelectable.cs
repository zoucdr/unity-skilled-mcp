using UnityEngine;

namespace IronFront
{
    /// <summary>所有可选物体的基类：单位与建筑共用选择、血条、受击接口。</summary>
    public abstract class RtsSelectable : MonoBehaviour
    {
        public Team Team = Team.Blue;
        public bool IsSelected;
        public float Hp;
        public float MaxHp = 100f;

        public abstract string DisplayName { get; }
        public abstract float BarHeight { get; }
        public abstract bool IsAlive { get; }

        public float HpRatio
        {
            get { return MaxHp <= 0f ? 0f : Mathf.Clamp01(Hp / MaxHp); }
        }

        public abstract void TakeDamage(float amount, RtsSelectable source);

        public void SetSelected(bool on)
        {
            IsSelected = on;
            OnSelectionVisual(on);
        }

        protected virtual void OnSelectionVisual(bool on)
        {
        }

        public virtual string InfoLine()
        {
            return DisplayName + "  HP " + Mathf.CeilToInt(Hp) + "/" + Mathf.CeilToInt(MaxHp);
        }
    }
}
