﻿using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection.SweepTasks;
using BepuUtilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection
{

    public struct DepthRefinerVertex
    {
        public Vector3 Support;
        public bool Exists;
    }

    public enum DepthRefinerNormalSource
    {
        TriangleFace = 3,
        Edge = 2,
        Vertex = 1,
    }

    public struct DepthRefinerStep
    {
        public DepthRefinerVertex A;
        public DepthRefinerVertex B;
        public DepthRefinerVertex C;
        public DepthRefinerVertex D;
        public DepthRefinerNormalSource NextNormalSource;
        public Vector3 ClosestPointOnTriangle;
        public Vector3 NextNormal;

        public float BestDepth;
        public Vector3 BestNormal;
    }


    public static class DepthRefiner<TShapeA, TShapeWideA, TSupportFinderA, TShapeB, TShapeWideB, TSupportFinderB>
        where TShapeA : IConvexShape
        where TShapeWideA : IShapeWide<TShapeA>
        where TSupportFinderA : ISupportFinder<TShapeA, TShapeWideA>
        where TShapeB : IConvexShape
        where TShapeWideB : IShapeWide<TShapeB>
        where TSupportFinderB : ISupportFinder<TShapeB, TShapeWideB>
    {
        public struct Vertex
        {
            public Vector3Wide Support;
            public Vector<int> Exists;
        }

        public struct Simplex
        {
            public Vertex A;
            public Vertex B;
            public Vertex C;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FillSlot(ref Vertex vertex, in Vector3Wide support)
        {
            //Note that this always fills empty slots. That's important- we avoid figuring out what subsimplex is active
            //and instead just treat it as a degenerate simplex with some duplicates. (Shares code with the actual degenerate path.)
            Vector3Wide.ConditionalSelect(vertex.Exists, vertex.Support, support, out vertex.Support);
            vertex.Exists = new Vector<int>(-1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ForceFillSlot(in Vector<int> shouldFill, ref Vertex vertex, in Vector3Wide support)
        {
            vertex.Exists = Vector.BitwiseOr(vertex.Exists, shouldFill);
            Vector3Wide.ConditionalSelect(shouldFill, support, vertex.Support, out vertex.Support);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Create(in Vector3Wide normal, in Vector3Wide support, in Vector<float> depth, out Simplex simplex)
        {
            //While only one slot is actually full, GetNextNormal expects every slot to have some kind of data-
            //for those slots which are not yet filled, it should be duplicates of other data.
            //(The sub-triangle case is treated the same as the degenerate case.)
            simplex.A.Support = support;
            simplex.B.Support = support;
            simplex.C.Support = support;
            simplex.A.Exists = new Vector<int>(-1);
            simplex.B.Exists = Vector<int>.Zero;
            simplex.C.Exists = Vector<int>.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FindSupport(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB, in Vector3Wide direction, out Vector3Wide support)
        {
            //support(N, A) - support(-N, B)
            supportFinderA.ComputeLocalSupport(a, direction, out var extremeA);
            Vector3Wide.Negate(direction, out var negatedDirection);
            supportFinderB.ComputeSupport(b, localOrientationB, negatedDirection, out var extremeB);
            Vector3Wide.Add(extremeB, localOffsetB, out extremeB);

            Vector3Wide.Subtract(extremeA, extremeB, out support);
        }

        struct HasNewSupport { }
        struct HasNoNewSupport { }
        static void GetNextNormal<T>(ref Simplex simplex, in Vector3Wide localOffsetB, in Vector3Wide support, ref Vector<int> terminatedLanes,
            in Vector3Wide bestNormal, in Vector<float> bestDepth, in Vector<float> convergenceThreshold,
            out Vector3Wide nextNormal, out DepthRefinerStep step)
        {
            {
                //DEBUG STUFF
                step = default;
                Vector3Wide.ReadFirst(simplex.A.Support, out step.A.Support);
                Vector3Wide.ReadFirst(simplex.B.Support, out step.B.Support);
                Vector3Wide.ReadFirst(simplex.C.Support, out step.C.Support);
                Vector3Wide.ReadFirst(support, out step.D.Support);
                step.A.Exists = simplex.A.Exists[0] < 0;
                step.B.Exists = simplex.B.Exists[0] < 0;
                step.C.Exists = simplex.C.Exists[0] < 0;
                step.D.Exists = typeof(T) == typeof(HasNewSupport);
            }

            //In the penetrating case, the search target is the closest point to the origin on the so-far-best bounding plane.
            //In the separated case, it's just the origin itself.
            //Termination conditions are based on the distance to the search target. In the penetrating case, we try to approach zero distance.
            //The separated case makes use of the fact that the bestDepth and distance to closest point only converge when the offset and best normal align.
            Vector3Wide.Scale(bestNormal, Vector.Max(Vector<float>.Zero, bestDepth), out var searchTarget);
            var terminationEpsilon = Vector.ConditionalSelect(Vector.LessThan(bestDepth, Vector<float>.Zero), convergenceThreshold - bestDepth, convergenceThreshold);
            var terminationEpsilonSquared = terminationEpsilon * terminationEpsilon;

            if (typeof(T) == typeof(HasNewSupport))
            {
                var simplexFull = Vector.BitwiseAnd(simplex.A.Exists, Vector.BitwiseAnd(simplex.B.Exists, simplex.C.Exists));
                //Fill any empty slots with the new support. Combines partial simplex case with degenerate simplex case.
                FillSlot(ref simplex.A, support);
                FillSlot(ref simplex.B, support);
                FillSlot(ref simplex.C, support);

                var activeFullSimplex = Vector.AndNot(simplexFull, terminatedLanes);
                if (Vector.LessThanAny(activeFullSimplex, Vector<int>.Zero))
                {
                    //At least one active lane has a full simplex and an incoming new sample.

                    //Choose the subtriangle based on the edge plane tests of AD, BD, and CD, where D is the new support point.

                    Vector3Wide.Subtract(simplex.B.Support, simplex.A.Support, out var abEarly);
                    Vector3Wide.Subtract(simplex.A.Support, simplex.C.Support, out var caEarly);
                    Vector3Wide.Subtract(support, simplex.A.Support, out var ad);
                    Vector3Wide.Subtract(support, simplex.B.Support, out var bd);
                    Vector3Wide.Subtract(support, simplex.C.Support, out var cd);
                    Vector3Wide.CrossWithoutOverlap(abEarly, caEarly, out var triangleNormalEarly);
                    //(ad x n) * (d - searchTarget) = (n x (d - searchTarget)) * ad
                    Vector3Wide.Subtract(support, searchTarget, out var targetToSupport);
                    Vector3Wide.CrossWithoutOverlap(triangleNormalEarly, targetToSupport, out var nxOffset);
                    Vector3Wide.Dot(nxOffset, ad, out var adPlaneTest);
                    Vector3Wide.Dot(nxOffset, bd, out var bdPlaneTest);
                    Vector3Wide.Dot(nxOffset, cd, out var cdPlaneTest);

                    var useABD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(adPlaneTest, Vector<float>.Zero), Vector.LessThan(bdPlaneTest, Vector<float>.Zero));
                    var useBCD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(bdPlaneTest, Vector<float>.Zero), Vector.LessThan(cdPlaneTest, Vector<float>.Zero));
                    var useCAD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(cdPlaneTest, Vector<float>.Zero), Vector.LessThan(adPlaneTest, Vector<float>.Zero));

                    //Because the best normal may have changed due to the latest sample, ABC's portal may not contain the best normal anymore, which may mean
                    //that none of the subtriangles do either. This is fairly rare and the fallback heuristic doesn't matter much- this won't cause cycles because
                    //it can only occur on iterations where depth improvement has been made (and thus the best normal has changed).
                    //So we'll do something stupid and cheap!
                    useABD = Vector.ConditionalSelect(Vector.OnesComplement(Vector.BitwiseOr(Vector.BitwiseOr(useABD, useBCD), useCAD)), new Vector<int>(-1), useABD);

                    ForceFillSlot(Vector.BitwiseAnd(useBCD, simplexFull), ref simplex.A, support);
                    ForceFillSlot(Vector.BitwiseAnd(useCAD, simplexFull), ref simplex.B, support);
                    ForceFillSlot(Vector.BitwiseAnd(useABD, simplexFull), ref simplex.C, support);
                }
            }
            else
            {
                FillSlot(ref simplex.A, simplex.A.Support);
                FillSlot(ref simplex.B, simplex.A.Support);
                FillSlot(ref simplex.C, simplex.A.Support);
            }
            Vector3Wide.Subtract(simplex.B.Support, simplex.A.Support, out var ab);
            Vector3Wide.Subtract(simplex.A.Support, simplex.C.Support, out var ca);
            Vector3Wide.Subtract(simplex.C.Support, simplex.B.Support, out var bc);
            Vector3Wide.CrossWithoutOverlap(ab, ca, out var triangleNormal);
            Vector3Wide.LengthSquared(triangleNormal, out var triangleNormalLengthSquared);

            //Compute the plane sign tests. Note that these are barycentric weights that have not been scaled by the inverse triangle normal length squared;
            //we do not have to compute the correct magnitude to know the sign, and the sign is all we care about.
            Vector3Wide.Subtract(simplex.A.Support, searchTarget, out var targetToA);
            Vector3Wide.Subtract(simplex.C.Support, searchTarget, out var targetToC);
            Vector3Wide.CrossWithoutOverlap(ab, targetToA, out var abxta);
            Vector3Wide.CrossWithoutOverlap(ca, targetToC, out var caxtc);
            Vector3Wide.Dot(abxta, triangleNormal, out var abPlaneTest);
            Vector3Wide.Dot(caxtc, triangleNormal, out var caPlaneTest);
            var bcPlaneTest = triangleNormalLengthSquared - caPlaneTest - abPlaneTest;
            var outsideAB = Vector.LessThan(abPlaneTest, Vector<float>.Zero);
            var outsideBC = Vector.LessThan(bcPlaneTest, Vector<float>.Zero);
            var outsideCA = Vector.LessThan(caPlaneTest, Vector<float>.Zero);

            Vector3Wide.LengthSquared(ab, out var abLengthSquared);
            Vector3Wide.LengthSquared(bc, out var bcLengthSquared);
            Vector3Wide.LengthSquared(ca, out var caLengthSquared);
            var longestEdgeLengthSquared = Vector.Max(Vector.Max(abLengthSquared, bcLengthSquared), caLengthSquared);
            var simplexDegenerate = Vector.LessThanOrEqual(triangleNormalLengthSquared, longestEdgeLengthSquared * 1e-10f);
            var degeneracyEpsilon = new Vector<float>(1e-14f);
            var simplexIsAVertex = Vector.LessThan(longestEdgeLengthSquared, degeneracyEpsilon);
            var simplexIsAnEdge = Vector.AndNot(simplexDegenerate, simplexIsAVertex);

            Vector3Wide.Dot(triangleNormal, localOffsetB, out var calibrationDot);
            var shouldCalibrate = Vector.LessThan(calibrationDot, Vector<float>.Zero);
            Vector3Wide.ConditionallyNegate(Vector.LessThan(calibrationDot, Vector<float>.Zero), ref triangleNormal);

            //If the simplex is degenerate and has length, use the longest edge. Otherwise, just use an edge whose plane is violated.
            //Note that if there are two edges with violated edge planes, simply picking one does not guarantee that the resulting closest point
            //will be the globally closest point on the triangle. It *usually* works out, though, and when it fails, the cost
            //is generally just an extra iteration or two- not a huge deal, and we get to avoid doing more than one edge test every iteration.
            var useAB = Vector.BitwiseOr(Vector.AndNot(outsideAB, simplexDegenerate), Vector.BitwiseAnd(Vector.Equals(longestEdgeLengthSquared, abLengthSquared), simplexDegenerate));
            var useBC = Vector.BitwiseOr(Vector.AndNot(outsideBC, simplexDegenerate), Vector.BitwiseAnd(Vector.Equals(longestEdgeLengthSquared, bcLengthSquared), simplexDegenerate));
            var targetOutsideTriangleEdges = Vector.BitwiseOr(outsideAB, Vector.BitwiseOr(outsideBC, outsideCA));

            //Compute the direction from the origin to the closest point on the triangle.
            //If the simplex is degenerate and just a vertex, pick the first simplex entry as representative.
            Vector3Wide.Negate(targetToA, out var triangleToTarget);

            {
                //DEBUG STUFF
                Vector3Wide.ReadFirst(simplex.A.Support, out step.ClosestPointOnTriangle);
            }

            var relevantFeatures = Vector<int>.One;

            var useEdge = Vector.BitwiseOr(targetOutsideTriangleEdges, simplexIsAnEdge);
            //If this is a vertex case and the sample is right on top of the origin, immediately quit.
            Vector3Wide.LengthSquared(targetToA, out var vertexLengthSquared);
            terminatedLanes = Vector.BitwiseOr(terminatedLanes, Vector.BitwiseAnd(simplexIsAVertex, Vector.LessThan(vertexLengthSquared, terminationEpsilonSquared)));

            if (Vector.LessThanAny(Vector.AndNot(useEdge, terminatedLanes), Vector<int>.Zero))
            {
                var edgeLengthSquared = Vector.ConditionalSelect(useAB, abLengthSquared, Vector.ConditionalSelect(useBC, bcLengthSquared, caLengthSquared));
                var inverseEdgeLengthSquared = Vector<float>.One / edgeLengthSquared;
                Vector3Wide.ConditionalSelect(useAB, ab, ca, out var edgeOffset);
                Vector3Wide.ConditionalSelect(useAB, simplex.A.Support, simplex.C.Support, out var edgeStart);
                Vector3Wide.ConditionalSelect(useBC, bc, edgeOffset, out edgeOffset);
                Vector3Wide.ConditionalSelect(useBC, simplex.B.Support, edgeStart, out edgeStart);

                Vector3Wide.Subtract(searchTarget, edgeStart, out var edgeStartToSearchTarget);
                Vector3Wide.Dot(edgeStartToSearchTarget, edgeOffset, out var startDot);
                var t = Vector.ConditionalSelect(Vector.GreaterThan(edgeLengthSquared, Vector<float>.Zero), Vector.Max(Vector<float>.Zero, Vector.Min(Vector<float>.One, startDot * inverseEdgeLengthSquared)), Vector<float>.Zero);
                Vector3Wide.Scale(edgeOffset, t, out var scaledOffset);
                Vector3Wide.Add(scaledOffset, edgeStart, out var nearestPointOnEdge);
                Vector3Wide.Subtract(searchTarget, nearestPointOnEdge,  out var triangleToTargetCandidate);
                Vector3Wide.LengthSquared(triangleToTargetCandidate, out var candidateLengthSquared);
                var targetIsOnEdge = Vector.LessThan(candidateLengthSquared, terminationEpsilonSquared);
                //If the search target is on the edge, we can immediately quit.
                terminatedLanes = Vector.BitwiseOr(terminatedLanes, Vector.BitwiseAnd(useEdge, targetIsOnEdge));

                var originNearestStart = Vector.Equals(t, Vector<float>.Zero);
                var originNearestEnd = Vector.Equals(t, Vector<float>.One);
                var featureForAB = Vector.ConditionalSelect(originNearestStart, Vector<int>.One, Vector.ConditionalSelect(originNearestEnd, new Vector<int>(2), new Vector<int>(1 + 2)));
                var featureForBC = Vector.ConditionalSelect(originNearestStart, new Vector<int>(2), Vector.ConditionalSelect(originNearestEnd, new Vector<int>(4), new Vector<int>(2 + 4)));
                var featureForCA = Vector.ConditionalSelect(originNearestStart, new Vector<int>(4), Vector.ConditionalSelect(originNearestEnd, Vector<int>.One, new Vector<int>(4 + 1)));
                relevantFeatures = Vector.ConditionalSelect(useEdge, Vector.ConditionalSelect(useAB, featureForAB, Vector.ConditionalSelect(useBC, featureForBC, featureForCA)), relevantFeatures);
                Vector3Wide.ConditionalSelect(useEdge, triangleToTargetCandidate, triangleToTarget, out triangleToTarget);

                {
                    //DEBUG STUFF
                    if (useEdge[0] < 0)
                    {
                        Vector3Wide.ReadFirst(nearestPointOnEdge, out step.ClosestPointOnTriangle);
                    }
                }
            }

            //We've examined the vertex and edge case, now we need to check the triangle face case.
            var targetContainedInEdgePlanes = Vector.AndNot(Vector.OnesComplement(targetOutsideTriangleEdges), simplexDegenerate);
            if (Vector.LessThanAny(Vector.AndNot(targetContainedInEdgePlanes, terminatedLanes), Vector<int>.Zero))
            {
                //At least one lane needs a face test.
                //Note that we don't actually need to compute the closest point here- we can just use the triangleNormal.
                //We do need to calculate the distance from the closest point to the search target, but that's just:

                //||searchTarget-closestOnTriangle||^2 = ||(dot(n/||n||, searchTarget - a) * n/||n||)||^2
                //||(dot(n, searchTarget - a) / ||n||^2) * n||^2
                //||n||^2 * (dot(n, searchTarget - a) / ||n||^2)^2
                //dot(n, searchTarget - a)^2 / ||n||^2
                //Then the comparison can performed with a multiplication rather than a division.
                Vector3Wide.Dot(targetToA, triangleNormal, out var targetToADot);
                var targetOnTriangleSurface = Vector.LessThan(targetToADot * targetToADot, terminationEpsilonSquared * triangleNormalLengthSquared);
                terminatedLanes = Vector.BitwiseOr(Vector.BitwiseAnd(targetContainedInEdgePlanes, targetOnTriangleSurface), terminatedLanes);
                Vector3Wide.ConditionalSelect(targetContainedInEdgePlanes, triangleNormal, triangleToTarget, out triangleToTarget);
                relevantFeatures = Vector.ConditionalSelect(targetContainedInEdgePlanes, new Vector<int>(1 + 2 + 4), relevantFeatures);

                {
                    //DEBUG STUFF
                    if (targetContainedInEdgePlanes[0] < 0)
                    {
                        Vector3Wide.ReadFirst(triangleNormal, out var triangleNormalNarrow);
                        Vector3Wide.ReadFirst(searchTarget, out var searchTargetNarrow);
                        step.ClosestPointOnTriangle = triangleNormalNarrow * targetToADot[0] / triangleNormalLengthSquared[0] + searchTargetNarrow;
                    }
                }
            }

            //In fairly rare cases near penetrating convergence, it's possible for the triangle->target offset to point nearly 90 degrees away from the previous best.
            //This doesn't break convergence, but it can slow it down. To avoid it, use the offset to tilt the normal rather than using the offset directly.
            Vector3Wide.Scale(triangleToTarget, new Vector<float>(4f), out var pushOffset);
            Vector3Wide.Add(searchTarget, pushOffset, out var pushNormalCandidate);
            Vector3Wide.ConditionalSelect(Vector.BitwiseOr(Vector.LessThanOrEqual(bestDepth, Vector<float>.Zero), targetContainedInEdgePlanes), triangleToTarget, pushNormalCandidate, out triangleToTarget);

            if (Vector.EqualsAny(terminatedLanes, Vector<int>.Zero))
            {
                simplex.A.Exists = Vector.GreaterThan(Vector.BitwiseAnd(relevantFeatures, Vector<int>.One), Vector<int>.Zero);
                simplex.B.Exists = Vector.GreaterThan(Vector.BitwiseAnd(relevantFeatures, new Vector<int>(2)), Vector<int>.Zero);
                simplex.C.Exists = Vector.GreaterThan(Vector.BitwiseAnd(relevantFeatures, new Vector<int>(4)), Vector<int>.Zero);
                //No active lanes can have a zero length targetToTriangle, so we can normalize safely.
                Vector3Wide.LengthSquared(triangleToTarget, out var lengthSquared);
                Vector3Wide.Scale(triangleToTarget, Vector<float>.One / Vector.SquareRoot(lengthSquared), out nextNormal);
            }


            {
                //DEBUG STUFF
                var features = relevantFeatures[0];
                var vertexCount = (features & 1) + ((features & 2) >> 1) + ((features & 4) >> 2);
                step.NextNormalSource = (DepthRefinerNormalSource)vertexCount;
                Vector3Wide.ReadFirst(nextNormal, out step.NextNormal);
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FindMinimumDepth(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB,
            in Vector3Wide initialNormal, in Vector<int> inactiveLanes, in Vector<float> searchEpsilon, in Vector<float> minimumDepthThreshold, out Vector<float> depth, out Vector3Wide refinedNormal, List<DepthRefinerStep> steps, int maximumIterations = 50)
        {
#if DEBUG
            Vector3Wide.LengthSquared(initialNormal, out var initialNormalLengthSquared);
            Debug.Assert(Vector.LessThanAll(Vector.BitwiseOr(inactiveLanes, Vector.LessThan(Vector.Abs(initialNormalLengthSquared - Vector<float>.One), new Vector<float>(1e-6f))), Vector<int>.Zero));
#endif
            FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, initialNormal, out var initialSupport);
            Vector3Wide.Dot(initialSupport, initialNormal, out var initialDepth);
            Create(initialNormal, initialSupport, initialDepth, out var simplex);
            FindMinimumDepth(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, ref simplex, initialNormal, initialDepth, inactiveLanes, searchEpsilon, minimumDepthThreshold, out depth, out refinedNormal, steps, maximumIterations);
        }

        public static void FindMinimumDepth(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB,
            ref Simplex simplex, Vector3Wide initialNormal, in Vector<float> initialDepth,
            in Vector<int> inactiveLanes, in Vector<float> convergenceThreshold, in Vector<float> minimumDepthThreshold, out Vector<float> refinedDepth, out Vector3Wide refinedNormal, List<DepthRefinerStep> steps, int maximumIterations = 50)
        {
            var depthBelowThreshold = Vector.LessThan(initialDepth, minimumDepthThreshold);
            var terminatedLanes = Vector.BitwiseOr(depthBelowThreshold, inactiveLanes);

            refinedNormal = initialNormal;
            refinedDepth = initialDepth;
            if (Vector.LessThanAll(terminatedLanes, Vector<int>.Zero))
            {
                return;
            }

            GetNextNormal<HasNoNewSupport>(ref simplex, localOffsetB, default, ref terminatedLanes, refinedNormal, refinedDepth, convergenceThreshold, out var normal, out var debugStep);
            debugStep.BestDepth = refinedDepth[0];
            Vector3Wide.ReadSlot(ref refinedNormal, 0, out debugStep.BestNormal);
            steps?.Add(debugStep);

            for (int i = 0; i < maximumIterations; ++i)
            {
                if (Vector.LessThanAll(terminatedLanes, Vector<int>.Zero))
                    break;
                FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, normal, out var support);
                Vector3Wide.Dot(support, normal, out var depth);
                //Console.WriteLine($"Depth: {depth[0]}");

                var useNewDepth = Vector.LessThan(depth, refinedDepth);
                refinedDepth = Vector.ConditionalSelect(useNewDepth, depth, refinedDepth);
                Vector3Wide.ConditionalSelect(useNewDepth, normal, refinedNormal, out refinedNormal);
                terminatedLanes = Vector.BitwiseOr(Vector.LessThanOrEqual(refinedDepth, minimumDepthThreshold), terminatedLanes);
                if (Vector.LessThanAll(terminatedLanes, Vector<int>.Zero))
                    break;

                GetNextNormal<HasNewSupport>(ref simplex, localOffsetB, support, ref terminatedLanes, refinedNormal, refinedDepth, convergenceThreshold, out normal, out debugStep);

                debugStep.BestDepth = refinedDepth[0];
                Vector3Wide.ReadSlot(ref refinedNormal, 0, out debugStep.BestNormal);

                steps?.Add(debugStep);
            }
        }
    }
}