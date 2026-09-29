using UnityEngine;

namespace Autobahn
{
    // Where the world has to be built and drawn around: the person when they are out of the car
    // (on foot, in a boat, flying), the car otherwise. From the air more of it is needed.
    public static class Focus
    {
        public static Vector3 Of(Transform car)
        {
            var foot = FootPlayer.Active;
            if (foot) return foot.InPlane ? foot.InPlane.transform.position : foot.transform.position;
            return Car(car);
        }

        // The car itself, wherever the driver is. Its body's position: a car just put down
        // somewhere else (a new run, a teleport) is there before its transform catches up, and
        // the world streaming in round it must see that before the next physics step.
        public static Vector3 Car(Transform car)
        {
            if (!car) return Vector3.zero;
            var body = car.GetComponent<Rigidbody>();
            return body ? body.position : car.position;
        }

        // Extra reach for streaming when high up (an aircraft sees far).
        public static float Extra(Transform car)
        {
            var foot = FootPlayer.Active;
            if (!foot || !foot.InPlane) return 0;
            return Mathf.Clamp(foot.InPlane.Altitude * 4, 0, 1200);
        }
    }
}
