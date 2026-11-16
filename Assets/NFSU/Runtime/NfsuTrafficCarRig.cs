using System;
using System.Collections.Generic;
using UnityEngine;

namespace NFSU.Runtime
{
    /// <summary>
    /// Visual wheel rig of an imported NFSU traffic car.
    /// <para>
    /// The importer (<c>NFSU.Editor.NfsuCarBuilder</c>) builds the node chain
    /// <c>Root -&gt; Mount (steer pivot) -&gt; Spin (roll) -&gt; Mesh</c> per corner and wires it
    /// here, so nothing is looked up by name at runtime.
    /// </para>
    /// <para>
    /// Axis conventions are the importer's: the car model points along <c>+X</c> with <c>+Y</c> up
    /// (the game stores it Z-up and the builder bakes the (x,y,z) -&gt; (x,z,-y) basis change into
    /// the vertices). Steer is therefore a rotation about <c>+Y</c> - a positive angle turns left,
    /// which is also the sign the game's own steering uses - and the roll is a rotation about
    /// <c>+Z</c>, negative for forward travel, because the tyre's stack axis in the game's frame is
    /// its lateral axis.
    /// </para>
    /// </summary>
    public sealed class NfsuTrafficCarRig : MonoBehaviour
    {
        [Serializable]
        public struct WheelNode
        {
            public string name;
            public bool isFront;
            public Transform mount;       // steer pivot, carries the wheel position
            public Transform spin;        // rolls the wheel
            public float radius;          // metres, from the tyre mesh
        }

        public WheelNode[] Wheels = Array.Empty<WheelNode>();

        [Tooltip("Metres per second along the model's +X. Drives how fast the tyres roll.")]
        public float Speed;

        [Tooltip("Front-wheel angle in degrees, positive turns left.")]
        public float SteerAngle;

        [Tooltip("Spin the tyres even with no driver attached.")]
        public bool Animate = true;

        float _rollDeg;
        readonly List<Renderer> _cached = new List<Renderer>();
        Bounds _bounds;
        bool _boundsReady;

        public float WheelRadius
        {
            get
            {
                float r = 0f;
                if (Wheels != null)
                    foreach (var w in Wheels)
                        r = Mathf.Max(r, w.radius);
                return r;
            }
        }

        /// <summary>Visual bounds of the whole car, cached (used by debug tools and spawners).</summary>
        public Bounds Bounds
        {
            get
            {
                if (_boundsReady)
                    return _bounds;
                _cached.Clear();
                GetComponentsInChildren(true, _cached);
                bool any = false;
                var b = new Bounds(transform.position, Vector3.zero);
                foreach (var r in _cached)
                {
                    if (!r.enabled)
                        continue;
                    if (!any)
                    {
                        b = r.bounds;
                        any = true;
                    }
                    else
                        b.Encapsulate(r.bounds);
                }
                _bounds = b;
                _boundsReady = any;
                return _bounds;
            }
        }

        /// <summary>
        /// Local y of the lowest tyre contact patch (mount y minus tyre radius): the road plane the
        /// model implies. The traffic art bakes a flat wheel patch under every corner into its
        /// lower-detail body, and that patch's plane IS the road, so the importer places the axle at
        /// patch z + tyre radius and the tread lands exactly on the patch. Measured over the 13
        /// traffic bundles that bake a patch, that plane sits 0.12-0.31 m BELOW the model's own
        /// lowest body vertex and 0.12-0.39 m below the physics box floor - real ground clearance.
        /// This is therefore the offset a driver or a review surface should use.
        /// </summary>
        public float ContactOffset
        {
            get
            {
                if (!_contactReady)
                    RefreshContact();
                return _contact;
            }
        }

        bool _contactReady;
        float _contact;

        /// <summary>
        /// Local y of the physics box's bottom plane. That box is a chassis box, NOT the road: over
        /// the 15 traffic cars its floor sits 0.12-0.39 m above the plane the wheel patches put the
        /// road on (measured: 4DR_SEDAN +0.287, SUV +0.394, PANELVAN +0.285, HATCHBACK +0.117 m), so
        /// resting a car on this plane buries its tyres by that much. Keep it for what the shipped
        /// collision volume is (a BoxCollider hit-test, a car lift); place cars on the road with
        /// <see cref="ContactOffset"/>. Falls back to the wheel contact when the car has no
        /// BoxCollider.
        /// </summary>
        public float GroundOffset
        {
            get
            {
                if (!_groundReady)
                    RefreshGround();
                return _ground;
            }
        }

        bool _groundReady;
        float _ground;

        public void InvalidateBounds()
        {
            _boundsReady = false;
            _contactReady = false;
            _groundReady = false;
        }

        void RefreshGround()
        {
            var col = GetComponent<BoxCollider>();
            if (col != null)
            {
                _ground = col.center.y - col.size.y * 0.5f;
                _groundReady = true;
                return;
            }
            _ground = ContactOffset;
            _groundReady = false;       // a collider may be added later
        }

        void RefreshContact()
        {
            _contact = 0f;
            bool any = false;
            if (Wheels != null)
                foreach (var w in Wheels)
                {
                    if (w.mount == null)
                        continue;
                    float y = w.mount.localPosition.y - (w.radius > 0.02f ? w.radius : 0.3f);
                    if (!any || y < _contact)
                        _contact = y;
                    any = true;
                }
            _contactReady = any;
        }

        public void SetSteer(float degrees)
        {
            SteerAngle = Mathf.Clamp(degrees, -38f, 38f);
        }

        void Update()
        {
            if (!Animate || Wheels == null)
                return;
            // One accumulator for the whole car: every tyre of a traffic car shares one radius, so
            // they stay in step without per-wheel state. _rollDeg is degrees travelled along the
            // tyre circumference, so the wrap keeps the angle small without losing sync.
            float r = WheelRadius > 0.02f ? WheelRadius : 0.3f;
            _rollDeg += Speed * Time.deltaTime / r * Mathf.Rad2Deg;
            if (_rollDeg > 360000f || _rollDeg < -360000f)
                _rollDeg -= 360000f;
            for (int i = 0; i < Wheels.Length; i++)
            {
                var w = Wheels[i];
                if (w.spin == null)
                    continue;
                w.spin.localRotation = Quaternion.AngleAxis(-_rollDeg, Vector3.forward);
                if (w.isFront && w.mount != null)
                    w.mount.localRotation = Quaternion.AngleAxis(SteerAngle, Vector3.up);
            }
        }

        void OnDrawGizmosSelected()
        {
            if (Wheels == null)
                return;
            Gizmos.color = Color.cyan;
            foreach (var w in Wheels)
            {
                if (w.spin == null)
                    continue;
                Gizmos.DrawWireSphere(w.spin.position, w.radius > 0.02f ? w.radius : 0.3f);
            }
        }
    }
}
