using UnityEngine;

namespace IronFront
{
    /// <summary>晶体矿脉：储量耗尽后自动消失。</summary>
    public class RtsResourceNode : MonoBehaviour
    {
        public int Remaining = 3000;
        public int Max = 3000;
        public float Radius = 2.2f;

        private Transform _visual;
        private float _pulse;
        private float _scaleBase;

        public bool Depleted
        {
            get { return Remaining <= 0; }
        }

        public void Init(int amount)
        {
            Remaining = amount;
            Max = amount;

            _visual = transform.Find("visual");
            if (_visual == null)
            {
                GameObject v = new GameObject("visual");
                v.transform.SetParent(transform, false);
                _visual = v.transform;

                RtsArt.Box(_visual, "c1", new Vector3(0f, 0.75f, 0f), new Vector3(0.55f, 1.5f, 0.55f), RtsArt.Crystal);
                RtsArt.Box(_visual, "c2", new Vector3(0.55f, 0.5f, 0.32f), new Vector3(0.4f, 1.0f, 0.4f), RtsArt.Crystal);
                RtsArt.Box(_visual, "c3", new Vector3(-0.45f, 0.42f, -0.38f), new Vector3(0.36f, 0.85f, 0.36f), RtsArt.Crystal);
                RtsArt.Box(_visual, "c4", new Vector3(-0.1f, 0.3f, 0.55f), new Vector3(0.3f, 0.6f, 0.3f), RtsArt.Crystal);

                Transform t1 = _visual.Find("c1");
                if (t1 != null) t1.localRotation = Quaternion.Euler(12f, 20f, 8f);
                Transform t2 = _visual.Find("c2");
                if (t2 != null) t2.localRotation = Quaternion.Euler(-8f, -15f, 14f);
                Transform t3 = _visual.Find("c3");
                if (t3 != null) t3.localRotation = Quaternion.Euler(6f, 32f, -12f);
            }

            _scaleBase = 1f;
            _pulse = 0f;
        }

        private void Update()
        {
            if (_visual == null) return;
            _pulse += Time.deltaTime;
            float k = 1f + Mathf.Sin(_pulse * 1.6f) * 0.02f;
            _visual.localScale = new Vector3(_scaleBase * k, _scaleBase, _scaleBase * k);
        }

        public int Extract(int amount)
        {
            if (Remaining <= 0) return 0;
            int take = Mathf.Min(amount, Remaining);
            Remaining -= take;
            _scaleBase = 0.6f + 0.4f * ((float)Remaining / Mathf.Max(1, Max));
            if (Remaining <= 0) Deplete();
            return take;
        }

        private void Deplete()
        {
            Remaining = 0;
            if (_visual != null) _visual.gameObject.SetActive(false);
            if (RtsGame.I != null) RtsGame.I.Unregister(this);
            Destroy(gameObject, 1.2f);
        }
    }
}
