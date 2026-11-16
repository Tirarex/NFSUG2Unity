// Drives an imported traffic car along a polyline of waypoints. The waypoints come from the
// decoded route data (NfsuRoute: the 0x00034121 corridors and the 0x00034132 road patches), whose
// coordinates are the scenery's Z-up metres; the importer converts them to Unity space with the
// same (x,y,z) -> (x,z,-y) rule it used for the meshes, so a path built that way needs no extra
// conversion here.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NFSU.Runtime
{
    /// <summary>
    /// Follows a waypoint list, keeps the car's wheels rolling and the front tyres pointed where the
    /// car is going, and holds the car at road height. Attach it to the same GameObject that holds
    /// <see cref="NfsuTrafficCarRig"/> (that is what the importer builds).
    /// </summary>
    public sealed class NfsuTrafficCarDriver : MonoBehaviour
    {
        NfsuTrafficCarRig _rig;

        [SerializeField] float _cruiseSpeed = 12f;       // m/s, ~43 km/h: city traffic
        [SerializeField] float _acceleration = 4f;       // m/s^2 towards the cruise speed
        [SerializeField] float _cornerReachM = 6f;       // cut the corner when the next leg is shorter
        [SerializeField] float _arriveM = 3f;            // waypoint is consumed inside this distance
        [SerializeField] bool _loop;
        [SerializeField] bool _stopAtEnd = true;

        IList<Vector3> _path;
        int _next;
        float _speed;

        public NfsuTrafficCarRig Rig
        {
            get { return _rig != null ? _rig : _rig = GetComponent<NfsuTrafficCarRig>(); }
        }

        public float CruiseSpeed { get { return _cruiseSpeed; } set { _cruiseSpeed = Mathf.Max(0f, value); } }
        public bool Loop { get { return _loop; } set { _loop = value; } }
        public bool Done { get; private set; }

        /// <summary>Index of the waypoint the car is heading for; useful for spawners and debugging.</summary>
        public int Waypoint { get { return _next; } }

        /// <summary>Current speed along the path, m/s.</summary>
        public float Speed { get { return _speed; } }

        /// <summary>
        /// Starts (or restarts) the run. The path is used as-is: no copies, so the caller keeps
        /// ownership. Waypoints are world positions whose y is the road surface.
        /// </summary>
        public void SetPath(IList<Vector3> path, bool loop = false)
        {
            _path = path;
            _loop = loop;
            _next = 0;
            _speed = 0f;
            Done = path == null || path.Count < 2;
        }

        void OnEnable()
        {
            var rig = Rig;
            if (rig != null)
                rig.Animate = true;     // the imported prefabs are built parked (Animate = false)
        }

        void Update()
        {
            var rig = Rig;
            if (rig == null || _path == null || _path.Count < 2)
                return;

            Vector3 pos = transform.position;
            Vector3 target = _path[_next];

            // Skip waypoints that are already behind the car - route corridors are sampled every
            // few metres, and at 15 m/s a 3 m arrival radius would leave the car steering at a
            // point it passed a frame ago.
            while (_next < _path.Count && Vector3.SqrMagnitude(target - pos) < _arriveM * _arriveM)
            {
                _next++;
                if (_next >= _path.Count)
                {
                    if (!_loop)
                    {
                        Done = true;
                        _speed = _stopAtEnd ? 0f : _speed;
                        rig.Speed = _speed;
                        return;
                    }
                    _next = 0;
                }
                target = _path[_next];
            }

            Vector3 flat = target - pos;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist < 1e-4f)
                return;
            Vector3 dir = flat / dist;

            // The model faces +X (the importer bakes the game's Z-up space into the vertices, where
            // +X is forward), while Unity's LookRotation aims +Z. A -90 degree yaw first maps the
            // car's +X onto +Z, so the composed rotation aims +X at dir.
            Quaternion want = Quaternion.LookRotation(dir, Vector3.up) *
                              Quaternion.Euler(0f, -90f, 0f);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, Mathf.Max(60f, _cruiseSpeed * 6f) * Time.deltaTime);

            // Steer from the heading error, not from the final waypoint: aiming at a point two
            // corners away makes the car cut the geometry. Look a little ahead instead, and only
            // when the next leg is short enough that its direction is the one to follow.
            Vector3 ahead = dir;
            if (_next + 1 < _path.Count)
            {
                Vector3 leg = _path[_next + 1] - _path[_next];
                leg.y = 0f;
                if (leg.sqrMagnitude < _cornerReachM * _cornerReachM && leg.sqrMagnitude > 1e-6f)
                    ahead = (dir * _cornerReachM + leg.normalized * 2f).normalized;
            }
            float angle = Vector3.SignedAngle(transform.rotation * Vector3.right, ahead,
                                              Vector3.up);
            rig.SteerAngle = Mathf.Clamp(angle * 2f, -38f, 38f);

            _speed = Mathf.MoveTowards(_speed, _cruiseSpeed, _acceleration * Time.deltaTime);
            float step = _speed * Time.deltaTime;
            if (step > dist)
                step = dist;
            // The waypoint's y is the road surface, and the surface the tyres actually run on is the
            // plane the model's own wheel patches define: the lowest tyre contact. (The physics box's
            // bottom plane, GroundOffset, is a chassis box 0.12-0.39 m above that, so resting the car
            // on it would bury the wheels.)
            transform.position = new Vector3(pos.x + dir.x * step,
                                             target.y - rig.ContactOffset,
                                             pos.z + dir.z * step);
            rig.Speed = _speed;
        }
    }
}
