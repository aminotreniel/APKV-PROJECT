using System;
using Unity.Mathematics;
using UnityEngine;

namespace TimeGhost
{
    [ExecuteAlways]
    public class ScatterDataOverride : MonoBehaviour
    {
        public struct PointCloudOverrideDataEntry
        {
            public float3 position;
            public quaternion orientation;
            public float scale;
            public float age;
            public float health;
            public Color32 color;
            public int partIndex;
            public float3 originalPosition;
        }
        
        public float age;
        public float health;
        public int partIndex;
        public Color32 color;

        public bool HasChanges => _hasChanges;

        private float3 _pos;
        private quaternion _orientation;
        private float _scale;
        private float _age;
        private float _health;
        private int _partIndex;
        private Color32 _color;

        private float3 _originalPosition;
        
        private bool _hasChanges = false;

        private MaterialPropertyBlock _mpb;
        private Transform _transf;

        public PointCloudOverrideDataEntry GetChangedDataEntry()
        {
            return new PointCloudOverrideDataEntry()
            {
                position = _pos,
                orientation = _orientation,
                scale = _scale,
                age = _age,
                health = _health,
                color = _color,
                partIndex = _partIndex,
                originalPosition = _originalPosition
            };

        }
        
        public void Setup(float3 position, quaternion orientation, float scale, float age, float health, int partIndex, Color32 color)
        {
            this.age = age;
            this.health = health;
            this.partIndex = partIndex;
            this.color = color;
            
            _transf = transform;
            _transf.localPosition = position;
            _transf.localRotation = orientation;
            _transf.localScale = scale * Vector3.one;
            _originalPosition = position;

            _mpb = new MaterialPropertyBlock();

            ApplyOverride();
        }

        
        
        private bool CheckChanges()
        {
            const float EPSILON = 1e-8f;
            bool extraDataChanged = math.abs(age - _age) > EPSILON || math.abs(health - _health) > EPSILON || partIndex != _partIndex || !color.Equals(_color);
            bool transformChanged = math.distancesq(_pos, _transf.localPosition) > EPSILON || math.distancesq(_orientation.value, ((quaternion)_transf.rotation).value) > EPSILON || _scale - _transf.localScale.x > EPSILON;
            return extraDataChanged || transformChanged;
        }
        
        private void Update()
        {
            if (CheckChanges())
            {
                _hasChanges = true;
                ApplyOverride();
            }
            
        }

        private void ApplyOverride()
        {
            _age = age;
            _health = health;
            _partIndex = partIndex;
            _color = color;

            _pos = _transf.localPosition;
            _orientation = _transf.localRotation;
            _scale = _transf.localScale.x;
            
            float colUint = math.asfloat(_color.r | _color.g << 8 | _color.b << 16 | _color.a << 24);
            
            _mpb.SetVector("_ScatteredInstanceExtraData", new Vector4(_age, _health, 0, 0));
            _mpb.SetFloat("_ScatteredInstanceExtraData2", colUint);
            _mpb.SetInt("_ScatteredInstanceExtraData3", (int)_partIndex);
            
            var mrs = GetComponentsInChildren<MeshRenderer>();

            foreach (var mr in mrs)
            {
                mr.SetPropertyBlock(_mpb);
            }
        }
        
    }
}