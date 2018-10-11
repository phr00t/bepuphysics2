﻿using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;
namespace BepuPhysics.Constraints
{
    /// <summary>
    /// Constrains the volume of a tetrahedron connecting the centers of four bodies to match a goal volume.
    /// </summary>
    public struct VolumeConstraint : IConstraintDescription<VolumeConstraint>
    {
        /// <summary>
        /// 6 times the target volume of the tetrahedra.
        /// </summary>
        public float TargetScaledVolume;
        public SpringSettings SpringSettings;

        public int ConstraintTypeId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return VolumeConstraintTypeProcessor.BatchTypeId;
            }
        }

        public Type BatchType => typeof(VolumeConstraintTypeProcessor);

        public void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var target = ref GetOffsetInstance(ref Buffer<VolumeConstraintPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Unsafe.As<Vector<float>, float>(ref target.TargetScaledVolume) = TargetScaledVolume;
            SpringSettingsWide.WriteFirst(SpringSettings, ref target.SpringSettings);
        }

        public void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out VolumeConstraint description)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var source = ref GetOffsetInstance(ref Buffer<VolumeConstraintPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            description.TargetScaledVolume = Unsafe.As<Vector<float>, float>(ref source.TargetScaledVolume);
            SpringSettingsWide.ReadFirst(source.SpringSettings, out description.SpringSettings);
        }
    }

    public struct VolumeConstraintPrestepData
    {
        public Vector<float> TargetScaledVolume;
        public SpringSettingsWide SpringSettings;
    }

    public struct VolumeConstraintProjection
    {
        public Vector3Wide JacobianB;
        public Vector3Wide JacobianC;
        public Vector3Wide JacobianD;
        public Vector<float> EffectiveMass;
        public Vector<float> BiasImpulse;
        public Vector<float> SoftnessImpulseScale;
        public Vector<float> InverseMassA;
        public Vector<float> InverseMassB;
        public Vector<float> InverseMassC;
        public Vector<float> InverseMassD;
    }

    public struct VolumeConstraintFunctions : IFourBodyConstraintFunctions<VolumeConstraintPrestepData, VolumeConstraintProjection, Vector<float>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prestep(Bodies bodies, ref FourBodyReferences bodyReferences, int count, float dt, float inverseDt,
            ref BodyInertias inertiaA, ref BodyInertias inertiaB, ref BodyInertias inertiaC, ref BodyInertias inertiaD,
            ref VolumeConstraintPrestepData prestep, out VolumeConstraintProjection projection)
        {
            bodies.GatherOffsets(ref bodyReferences, count, out var ab, out var ac, out var ad);

            //Volume of parallelepiped with vertices a, b, c, d is:
            //(ab x ac) * ad
            //A tetrahedron with the same edges will have one sixth of this volume. As a constant factor, it's not relevant. So the constraint is just:
            //OriginalVolume * 6 = (ab x ac) * ad
            //Taking the derivative to get the velocity constraint:
            //0 = d/dt(ab x ac) * ad + (ab x ac) * d/dt(ad)
            //0 = (d/dt(ab) x ac + ab x d/dt(ac)) * ad + (ab x ac) * d/dt(ad)
            //0 = (d/dt(ab) x ac) * ad + (ab x d/dt(ac)) * ad + (ab x ac) * d/dt(ad)
            //0 = (ac x ad) * d/dt(ab) + (ad x ab) * d/dt(ac) + (ab x ac) * d/dt(ad)
            //Giving the linear jacobians:
            //JA: -ac x ad - ad x ab - ab x ac
            //JB: ac x ad
            //JC: ad x ab
            //JD: ab x ac
            //JA could be compressed down to a form similar to the other jacobians with some algebra, but there's no need since it's cheap to just perform a few subtractions.
            //Note that we don't store out the jacobian for A either. A's jacobian is cheaply found from B, C, and D.
            //We're not blending the jacobians into the effective mass or inverse mass either- even though that would save ALU time, the goal here is to minimize memory bandwidth since that
            //tends to be the bottleneck for any multithreaded simulation. (Despite being a 1DOF constraint, this doesn't need to output inverse inertia tensors, so premultiplying isn't a win.)

            Vector3Wide.CrossWithoutOverlap(ac, ad, out projection.JacobianB);
            Vector3Wide.CrossWithoutOverlap(ad, ab, out projection.JacobianC);
            Vector3Wide.CrossWithoutOverlap(ab, ac, out projection.JacobianD);
            Vector3Wide.Add(projection.JacobianB, projection.JacobianC, out var negatedJA);
            Vector3Wide.Add(projection.JacobianD, negatedJA, out negatedJA);

            Vector3Wide.Dot(negatedJA, negatedJA, out var contributionA);
            Vector3Wide.Dot(projection.JacobianB, projection.JacobianB, out var contributionB);
            Vector3Wide.Dot(projection.JacobianC, projection.JacobianC, out var contributionC);
            Vector3Wide.Dot(projection.JacobianD, projection.JacobianD, out var contributionD);

            var inverseEffectiveMass = contributionA * inertiaA.InverseMass + contributionB * inertiaB.InverseMass + contributionC * inertiaC.InverseMass + contributionD * inertiaD.InverseMass;
            projection.InverseMassA = inertiaA.InverseMass;
            projection.InverseMassB = inertiaB.InverseMass;
            projection.InverseMassC = inertiaC.InverseMass;
            projection.InverseMassD = inertiaD.InverseMass;

            SpringSettingsWide.ComputeSpringiness(prestep.SpringSettings, dt, out var positionErrorToVelocity, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale);

            projection.EffectiveMass = effectiveMassCFMScale / inverseEffectiveMass;

            //Compute the position error and bias velocities. Note the order of subtraction when calculating error- we want the bias velocity to counteract the separation.
            Vector3Wide.Dot(projection.JacobianD, ad, out var unscaledVolume);
            projection.BiasImpulse = (unscaledVolume - prestep.TargetScaledVolume) * positionErrorToVelocity * projection.EffectiveMass;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyImpulse(ref BodyVelocities velocityA, ref BodyVelocities velocityB, ref BodyVelocities velocityC, ref BodyVelocities velocityD, 
            ref VolumeConstraintProjection projection, ref Vector3Wide negatedJacobianA, ref Vector<float> impulse)
        {
            Vector3Wide.Scale(negatedJacobianA, projection.InverseMassA, out var negativeVelocityChangeA);
            Vector3Wide.Scale(projection.JacobianB, projection.InverseMassB, out var velocityChangeB);
            Vector3Wide.Scale(projection.JacobianC, projection.InverseMassC, out var velocityChangeC);
            Vector3Wide.Scale(projection.JacobianD, projection.InverseMassD, out var velocityChangeD);
            Vector3Wide.Subtract(velocityA.Linear, negativeVelocityChangeA, out velocityA.Linear);
            Vector3Wide.Add(velocityB.Linear, velocityChangeB, out velocityB.Linear);
            Vector3Wide.Add(velocityC.Linear, velocityChangeC, out velocityC.Linear);
            Vector3Wide.Add(velocityD.Linear, velocityChangeD, out velocityD.Linear);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetNegatedJacobianA(in VolumeConstraintProjection projection, out Vector3Wide jacobianA)
        {
            Vector3Wide.Add(projection.JacobianB, projection.JacobianC, out jacobianA);
            Vector3Wide.Add(projection.JacobianD, jacobianA, out jacobianA);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart(ref BodyVelocities velocityA, ref BodyVelocities velocityB, ref BodyVelocities velocityC, ref BodyVelocities velocityD, ref VolumeConstraintProjection projection, ref Vector<float> accumulatedImpulse)
        {
            GetNegatedJacobianA(projection, out var negatedJacobianA);
            ApplyImpulse(ref velocityA, ref velocityB, ref velocityC, ref velocityD, ref projection, ref negatedJacobianA, ref accumulatedImpulse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve(ref BodyVelocities velocityA, ref BodyVelocities velocityB, ref BodyVelocities velocityC, ref BodyVelocities velocityD, ref VolumeConstraintProjection projection, ref Vector<float> accumulatedImpulse)
        {
            //csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - (csiaLinear + csiaAngular + csibLinear + csibAngular);
            GetNegatedJacobianA(projection, out var negatedJacobianA);
            Vector3Wide.Dot(negatedJacobianA, velocityA.Linear, out var negatedcontributionA);
            Vector3Wide.Dot(projection.JacobianB, velocityB.Linear, out var contributionB);
            Vector3Wide.Dot(projection.JacobianC, velocityC.Linear, out var contributionC);
            Vector3Wide.Dot(projection.JacobianD, velocityD.Linear, out var contributionD);
            var csv = contributionB + contributionC + contributionD - negatedcontributionA;
            var csi = projection.BiasImpulse - accumulatedImpulse * projection.SoftnessImpulseScale - csv;
            accumulatedImpulse += csi;

            ApplyImpulse(ref velocityA, ref velocityB, ref velocityC, ref velocityD, ref projection, ref negatedJacobianA, ref csi);
        }

    }


    /// <summary>
    /// Handles the solve iterations of a bunch of ball socket constraints.
    /// </summary>
    public class VolumeConstraintTypeProcessor : FourBodyTypeProcessor<VolumeConstraintPrestepData, VolumeConstraintProjection, Vector<float>, VolumeConstraintFunctions>
    {
        public const int BatchTypeId = 33;
    }
}
