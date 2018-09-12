﻿using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Collidables;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using BepuPhysics.Constraints;
using System.Diagnostics;
using System.Threading;
using BepuUtilities;

namespace BepuPhysics.CollisionDetection
{
    /*
     * The narrow phase operates on overlaps generated by the broad phase. 
     * Its job is to compute contact manifolds for overlapping collidables and to manage the constraints produced by those manifolds. 
     * 
     * The scheduling of collision detection jobs is conceptually asynchronous. There is no guarantee that a broad phase overlap provided to the narrow phase
     * will result in an immediate calculation of the manifold. This is useful for batching together many collidable pairs of the same type for simultaneous SIMD-friendly execution.
     * (Not all pairs are ideal fits for wide SIMD, but many common and simple ones are.)
     * 
     * The interface to the broad phase makes no guarantees about the nature of this batching. The narrow phase could immediately execute, or it could batch up Vector<float>.Count,
     * or maybe 32 in a row, or it could wait until all overlaps have been submitted before actually beginning work.
     * 
     * This deferred execution requires that the pending work be stored somehow. This is complicated by the fact that there are a variety of different top level pairs that handle
     * incoming contact manifold data and the resulting constraints in different ways. There are two main distinctions:
     * 1) Continuous collision detection mode. For the purposes of the narrow phase, each collidable can be thought of as discrete, inner sphere, substepping, or inner sphere + substepping.
     * -Discrete pairs take the result of the underlying manifold and directly manipulate regular contact constraints. 
     * -Inner sphere pairs, with sufficient relative linear velocity, can create one or two additional sphere-convex pairs per convex pair.
     * -Substepping pairs potentially generate a bunch of child pairs, depending on the collidable velocities, and then choose from the resulting manifolds. 
     * Once the best manifold is selected, constraint management is similar to the discrete case.
     * -Inner sphere + substepping pairs just do both of the above.
     * 2) Individual versus compound types. Compound pairs will tend to create child convex pairs and wait for their completion. This ensures the greatest number of simultaneous
     * SIMD-friendly manifold calculations. For example, four compound-compound pairs could result in 60 sphere-capsule subpairs which can then all be executed in a SIMD fashion.
     * 
     * These two build on each other- a compound-compound pair with inner sphere enabled will want to generate both the inner sphere pairs and the regular pairs simultaneously to avoid 
     * traversing any acceleration structures multiple times.
     * 
     * Note that its possible for the evaluation of a pair to generate more pairs. This is most easily seen in compound pairs or substep pairs, but we do permit less obvious cases.
     * For example, a potential optimization for substepping is only do as many substeps as are needed to find the first manifold with approaching contacts (or some other heuristic).
     * In order for such an optimization to be used, we must be willing to spawn more pairs if the first set of substeps we did didn't find any heuristically accepted manifolds.
     * In the limit, that would mean doing one substep at a time. (In practice, we'd probably just try to fill up the remainder of a SIMD batch.)
     * 
     * Another example: imagine a high-complexity convex-convex test that has highly divergent execution, but with smaller pieces which are not as divergent.
     * SIMD operations don't map well to divergent execution, so if the individual jobs are large enough, it could be worth it to spawn new pairs for the nondivergent pieces.
     * Most convexes aren't complicated enough to warrant this (often it's faster to simply execute all paths), but it may be relevant in the convex hull versus convex hull case.
     * 
     * In any case where more pairs are generated, evaluating just the current set of pairs is insufficient to guarantee completion. Instead, execution can be thought of like traversing a graph.
     * Each work-creating pair may create an entry on the execution stack if its 'execution threshold' is reached (the arbitrary size which, when reached, results in the execution of the 
     * stored pairs). When no jobs remain on the stack, take any available stored pair set and try to execute it- even if it hasn't yet reached its execution threshold. In this situation,
     * without further action it won't ever fill up, so there's no reason to wait. That execution may then spawn more work, which could create an element on the execution stack, and so on. 
     * Ideally, job sets are consumed in order of their probability of creating new work. That maximizes the number of SIMD-friendly executions.
     * 
     * In practice, there are two phases. The first phase takes in the broad phase-generated top level pairs. At this stage, we do not need to resort to executing incomplete bundles. 
     * Instead, we just continue to work on the top level pairs until none remain. The second phase kicks in here. Since no further top-level work is being generated, we start trying to 
     * flush all the remaining pairs, even if they are not at the execution threshold, as in the above traverse-and-reset approach.
     * 
     * All of the above works within the context of a single thread. There may be many threads in flight, but each one is guaranteed to be handling different top level pairs.
     * That means all of the pair storage is thread local and requires no synchronization. It is also mostly ephemeral- once the thread finishes, only a small amount of information needs
     * to be persisted to globally accessed memory. (Overlap->ConstraintHandle is one common piece of data, but some pairs may also persist other data like separating axes for early outs.
     * Such extra data is fairly rare, since it implies divergence in execution- which is something you don't want in a SIMD-friendly implementation. Likely only in things like hull-hull.)
     * 
     * Every narrow phase pair is responsible for managing the constraints that its computed manifolds require. 
     * This requires the ability to look up existing overlap->constraint relationships for three reasons:
     * 1) Any existing constraint, if it has the same number of contacts as the new manifold, should have its contact data updated.
     * 2) Any accumulated impulse from the previous frame's contact solve should be distributed over the new set of contacts for warm starting this frame's solve.
     * 3) Any change in contact count should result in the removal of the previous constraint (if present) and the addition of the new constraint (if above zero contacts).
     * This mapping is stored in a single dictionary. The previous frame's mapping is treated as read-only during the new frame's narrow phase execution, 
     * //so no synchronization is required to read it. The current frame updates pointerse in the dictionary and collects deferred adds on each worker thread for later flushing.
     * 
     * Constraints associated with 'stale' overlaps (those which were not updated during the current frame) are removed in a postpass.
     * 
     */


    public enum NarrowPhaseFlushJobType
    {
        RemoveConstraintsFromBodyLists,
        ReturnConstraintHandles,
        RemoveConstraintFromBatchReferencedHandles,
        RemoveConstraintsFromFallbackBatch,
        RemoveConstraintFromTypeBatch,
        FlushPairCacheChanges
    }

    public struct NarrowPhaseFlushJob
    {
        public NarrowPhaseFlushJobType Type;
        public int Index;
    }

    public abstract class NarrowPhase
    {
        public Simulation Simulation;
        public BufferPool Pool;
        public Bodies Bodies;
        public Statics Statics;
        public Solver Solver;
        public Shapes Shapes;
        public SweepTaskRegistry SweepTaskRegistry;
        public CollisionTaskRegistry CollisionTaskRegistry;
        public ConstraintRemover ConstraintRemover;
        internal FreshnessChecker FreshnessChecker;
        //TODO: It is possible that some types will benefit from per-overlap data, like separating axes. For those, we should have type-dedicated overlap dictionaries.
        //The majority of type pairs, however, only require a constraint handle.
        public PairCache PairCache;
        internal float timestepDuration;

        internal ContactConstraintAccessor[] contactConstraintAccessors;
        public void RegisterContactConstraintAccessor(ContactConstraintAccessor contactConstraintAccessor)
        {
            var id = contactConstraintAccessor.ConstraintTypeId;
            if (contactConstraintAccessors == null || contactConstraintAccessors.Length <= id)
                contactConstraintAccessors = new ContactConstraintAccessor[id + 1];
            if (contactConstraintAccessors[id] != null)
            {
                throw new InvalidOperationException($"Cannot register accessor for type id {id}; it is already registered by {contactConstraintAccessors[id]}.");
            }
            contactConstraintAccessors[id] = contactConstraintAccessor;
        }

        protected NarrowPhase()
        {
            flushWorkerLoop = FlushWorkerLoop;
        }

        public void Prepare(float dt, IThreadDispatcher threadDispatcher = null)
        {
            timestepDuration = dt;
            OnPrepare(threadDispatcher);
            PairCache.Prepare(threadDispatcher);
            ConstraintRemover.Prepare(threadDispatcher);
        }

        protected abstract void OnPrepare(IThreadDispatcher threadDispatcher);
        protected abstract void OnPreflush(IThreadDispatcher threadDispatcher, bool deterministic);
        protected abstract void OnPostflush(IThreadDispatcher threadDispatcher);


        bool deterministic;
        int flushJobIndex;
        QuickList<NarrowPhaseFlushJob, Buffer<NarrowPhaseFlushJob>> flushJobs;
        IThreadDispatcher threadDispatcher;
        Action<int> flushWorkerLoop;
        void FlushWorkerLoop(int workerIndex)
        {
            int jobIndex;
            var threadPool = threadDispatcher.GetThreadMemoryPool(workerIndex);
            while ((jobIndex = Interlocked.Increment(ref flushJobIndex)) < flushJobs.Count)
            {
                ExecuteFlushJob(ref flushJobs[jobIndex], threadPool);
            }
        }
        void ExecuteFlushJob(ref NarrowPhaseFlushJob job, BufferPool threadPool)
        {
            switch (job.Type)
            {
                case NarrowPhaseFlushJobType.RemoveConstraintsFromBodyLists:
                    ConstraintRemover.RemoveConstraintsFromBodyLists();
                    break;
                case NarrowPhaseFlushJobType.ReturnConstraintHandles:
                    ConstraintRemover.ReturnConstraintHandles(deterministic, threadPool);
                    break;
                case NarrowPhaseFlushJobType.RemoveConstraintFromBatchReferencedHandles:
                    ConstraintRemover.RemoveConstraintsFromBatchReferencedHandles();
                    break;
                case NarrowPhaseFlushJobType.RemoveConstraintsFromFallbackBatch:
                    ConstraintRemover.RemoveConstraintsFromFallbackBatch();
                    break;
                case NarrowPhaseFlushJobType.RemoveConstraintFromTypeBatch:
                    ConstraintRemover.RemoveConstraintsFromTypeBatch(job.Index);
                    break;
                case NarrowPhaseFlushJobType.FlushPairCacheChanges:
                    PairCache.FlushMappingChanges();
                    break;
            }

        }

        public void Flush(IThreadDispatcher threadDispatcher = null, bool deterministic = false)
        {
            OnPreflush(threadDispatcher, deterministic);
            //var start = Stopwatch.GetTimestamp();
            var jobPool = Pool.SpecializeFor<NarrowPhaseFlushJob>();
            QuickList<NarrowPhaseFlushJob, Buffer<NarrowPhaseFlushJob>>.Create(jobPool, 128, out flushJobs);
            PairCache.PrepareFlushJobs(ref flushJobs);
            //We indirectly pass the determinism state; it's used by the constraint remover bookkeeping.
            this.deterministic = deterministic;
            var removalBatchJobCount = ConstraintRemover.CreateFlushJobs();
            //Note that we explicitly add the constraint remover jobs here. 
            //The constraint remover can be used in two ways- sleeper style, and narrow phase style.
            //In sleeping, we're not actually removing constraints from the simulation completely, so it requires fewer jobs.
            //The constraint remover just lets you choose which jobs to call. The narrow phase needs all of them.
            flushJobs.EnsureCapacity(flushJobs.Count + removalBatchJobCount + 4, jobPool);
            flushJobs.AddUnsafely(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.RemoveConstraintsFromBodyLists });
            flushJobs.AddUnsafely(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.ReturnConstraintHandles });
            flushJobs.AddUnsafely(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.RemoveConstraintFromBatchReferencedHandles });
            if (Solver.ActiveSet.Batches.Count > Solver.FallbackBatchThreshold)
            {
                flushJobs.AddUnsafely(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.RemoveConstraintsFromFallbackBatch });
            }
            for (int i = 0; i < removalBatchJobCount; ++i)
            {
                flushJobs.AddUnsafely(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.RemoveConstraintFromTypeBatch, Index = i });
            }

            if (threadDispatcher == null)
            {
                for (int i = 0; i < flushJobs.Count; ++i)
                {
                    ExecuteFlushJob(ref flushJobs[i], Pool);
                }
            }
            else
            {
                flushJobIndex = -1;
                this.threadDispatcher = threadDispatcher;
                threadDispatcher.DispatchWorkers(flushWorkerLoop);
                this.threadDispatcher = null;
            }
            //var end = Stopwatch.GetTimestamp();
            //Console.WriteLine($"Flush stage 3 time (us): {1e6 * (end - start) / Stopwatch.Frequency}");
            flushJobs.Dispose(Pool.SpecializeFor<NarrowPhaseFlushJob>());

            PairCache.Postflush();
            ConstraintRemover.Postflush();

            OnPostflush(threadDispatcher);
        }

        public void Clear()
        {
            PairCache.Clear();
        }
        public void Dispose()
        {
            PairCache.Dispose();
            OnDispose();
        }

        protected abstract void OnDispose();



        //TODO: Configurable memory usage. It automatically adapts based on last frame state, but it's nice to be able to specify minimums when more information is known.

    }

    /// <summary>
    /// Turns broad phase overlaps into contact manifolds and uses them to manage constraints in the solver.
    /// </summary>
    /// <typeparam name="TCallbacks">Type of the callbacks to use.</typeparam>
    public partial class NarrowPhase<TCallbacks> : NarrowPhase where TCallbacks : struct, INarrowPhaseCallbacks
    {
        public TCallbacks Callbacks;
        public struct OverlapWorker
        {
            public CollisionBatcher<CollisionCallbacks> Batcher;
            public PendingConstraintAddCache PendingConstraints;
            public QuickList<int, Buffer<int>> PendingSetAwakenings;

            public OverlapWorker(int workerIndex, BufferPool pool, NarrowPhase<TCallbacks> narrowPhase)
            {
                Batcher = new CollisionBatcher<CollisionCallbacks>(pool, narrowPhase.Shapes, narrowPhase.CollisionTaskRegistry, narrowPhase.timestepDuration,
                    new CollisionCallbacks(workerIndex, pool, narrowPhase));
                PendingConstraints = new PendingConstraintAddCache(pool);
                QuickList<int, Buffer<int>>.Create(pool.SpecializeFor<int>(), 16, out PendingSetAwakenings);
            }
        }

        internal OverlapWorker[] overlapWorkers;

        public NarrowPhase(Simulation simulation, CollisionTaskRegistry collisionTaskRegistry, SweepTaskRegistry sweepTaskRegistry, TCallbacks callbacks,
             int initialSetCapacity, int minimumMappingSize = 2048, int minimumPendingSize = 128, int minimumPerTypeCapacity = 128)
            : base()
        {
            Simulation = simulation;
            Pool = simulation.BufferPool;
            Shapes = simulation.Shapes;
            Bodies = simulation.Bodies;
            Statics = simulation.Statics;
            Solver = simulation.Solver;
            ConstraintRemover = simulation.constraintRemover;
            Callbacks = callbacks;
            Callbacks.Initialize(simulation);
            CollisionTaskRegistry = collisionTaskRegistry;
            SweepTaskRegistry = sweepTaskRegistry;
            PairCache = new PairCache(simulation.BufferPool, initialSetCapacity, minimumMappingSize, minimumPendingSize, minimumPerTypeCapacity);
            FreshnessChecker = new FreshnessChecker(this);
            preflushWorkerLoop = PreflushWorkerLoop;
        }

        protected override void OnPrepare(IThreadDispatcher threadDispatcher)
        {
            var threadCount = threadDispatcher == null ? 1 : threadDispatcher.ThreadCount;
            //Resizes should be very rare, and having a single extra very small array isn't concerning.
            //(It's not an unmanaged type because it contains nonblittable references.)
            if (overlapWorkers == null || overlapWorkers.Length < threadCount)
                Array.Resize(ref overlapWorkers, threadCount);
            for (int i = 0; i < threadCount; ++i)
            {
                overlapWorkers[i] = new OverlapWorker(i, threadDispatcher != null ? threadDispatcher.GetThreadMemoryPool(i) : Pool, this);
            }
        }

        protected override void OnPostflush(IThreadDispatcher threadDispatcher)
        {
            //TODO: Constraint generators can actually be disposed immediately once the overlap finding process completes.
            //Here, we are disposing them late- that means we suffer a little more wasted memory use. 
            //If you actually wanted to address this, you could add in an OnPreflush or similar.
            var threadCount = threadDispatcher == null ? 1 : threadDispatcher.ThreadCount;
            for (int i = 0; i < threadCount; ++i)
            {
                overlapWorkers[i].Batcher.Callbacks.Dispose();
            }
        }

        protected override void OnDispose()
        {
            Callbacks.Dispose();
        }

        public unsafe void HandleOverlap(int workerIndex, CollidableReference a, CollidableReference b)
        {
            Debug.Assert(a.Packed != b.Packed, "Excuse me, broad phase, but an object cannot collide with itself!");
            //In order to guarantee contact manifold and constraint consistency across multiple frames, we must guarantee that the order of collidables submitted 
            //is the same every time. Since the provided handles do not move for the lifespan of the collidable in the simulation, they can be used as an ordering.
            //Between two bodies, simply put the lower handle in slot A always.
            //If one of the two objects is static, stick it in the second slot.       
            var aMobility = a.Mobility;
            var bMobility = b.Mobility;
            if ((aMobility != CollidableMobility.Static && bMobility != CollidableMobility.Static && a.Handle > b.Handle) ||
                aMobility == CollidableMobility.Static)
            {
                var temp = b;
                b = a;
                a = temp;
            }
            Debug.Assert(aMobility != CollidableMobility.Static || bMobility != CollidableMobility.Static, "Broad phase should not be able to generate static-static pairs.");
            if (!Callbacks.AllowContactGeneration(workerIndex, a, b))
                return;
            ref var overlapWorker = ref overlapWorkers[workerIndex];
            var pair = new CollidablePair(a, b);
            if (aMobility != CollidableMobility.Static && bMobility != CollidableMobility.Static)
            {
                //Both references are bodies.
                ref var bodyLocationA = ref Bodies.HandleToLocation[a.Handle];
                ref var bodyLocationB = ref Bodies.HandleToLocation[b.Handle];
                Debug.Assert(bodyLocationA.SetIndex == 0 || bodyLocationB.SetIndex == 0, "One of the two bodies must be active. Otherwise, something is busted!");
                ref var setA = ref Bodies.Sets[bodyLocationA.SetIndex];
                ref var setB = ref Bodies.Sets[bodyLocationB.SetIndex];
                AddBatchEntries(ref overlapWorker, ref pair,
                    ref setA.Collidables[bodyLocationA.Index], ref setB.Collidables[bodyLocationB.Index],
                    ref setA.Poses[bodyLocationA.Index], ref setB.Poses[bodyLocationB.Index],
                    ref setA.Velocities[bodyLocationA.Index], ref setB.Velocities[bodyLocationB.Index]);
            }
            else
            {
                //Since we disallow 2-static pairs and we guarantee the second slot holds the static if it exists, we know that A is a body and B is a static.
                //Further, we know that the body must be an *active* body, because inactive bodies and statics exist within the same static/inactive broad phase tree and are not tested
                //against each other.
                Debug.Assert(aMobility != CollidableMobility.Static && bMobility == CollidableMobility.Static);
                ref var bodyLocation = ref Bodies.HandleToLocation[a.Handle];
                Debug.Assert(bodyLocation.SetIndex == 0, "The body of a body-static pair must be active.");
                var staticIndex = Statics.HandleToIndex[b.Handle];

                //TODO: Ideally, the compiler would see this and optimize away the relevant math in AddBatchEntries. That's a longshot, though. May want to abuse some generics to force it.
                var zeroVelocity = default(BodyVelocity);
                ref var bodySet = ref Bodies.ActiveSet;
                AddBatchEntries(ref overlapWorker, ref pair,
                    ref bodySet.Collidables[bodyLocation.Index], ref Statics.Collidables[staticIndex],
                    ref bodySet.Poses[bodyLocation.Index], ref Statics.Poses[staticIndex],
                    ref bodySet.Velocities[bodyLocation.Index], ref zeroVelocity);
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void AddBatchEntries(ref OverlapWorker overlapWorker,
            ref CollidablePair pair, ref Collidable aCollidable, ref Collidable bCollidable,
            ref RigidPose poseA, ref RigidPose poseB, ref BodyVelocity velocityA, ref BodyVelocity velocityB)
        {
            Debug.Assert(pair.A.Packed != pair.B.Packed);
            //Note that we never create 'unilateral' CCD pairs. That is, if either collidable in a pair enables a CCD feature, we just act like both are using it.
            //That keeps things a little simpler. Unlike v1, we don't have to worry about the implications of 'motion clamping' here- no need for deeper configuration.
            var useSubstepping = aCollidable.Continuity.UseSubstepping || bCollidable.Continuity.UseSubstepping;
            var useInnerSphere = aCollidable.Continuity.UseInnerSphere || bCollidable.Continuity.UseInnerSphere;
            //Note that the pair's margin is the larger of the two involved collidables. This is based on two observations:
            //1) Values smaller than either contributor should never be used, because it may interfere with tuning. Difficult to choose substepping properties without a 
            //known minimum value for speculative margins.
            //2) The larger the margin, the higher the risk of ghost collisions. 
            //Taken together, max is implied.
            var speculativeMargin = Math.Max(aCollidable.SpeculativeMargin, bCollidable.SpeculativeMargin);
            //Create a continuation for the pair given the CCD state.
            if (useSubstepping && useInnerSphere)
            {
            }
            else if (useSubstepping)
            {

            }
            else if (useInnerSphere)
            {

            }
            else
            {
                //This pair uses no CCD beyond its speculative margin.
                var continuation = overlapWorker.Batcher.Callbacks.AddDiscrete(ref pair);
                overlapWorker.Batcher.Add(
                    aCollidable.Shape, bCollidable.Shape,
                    poseB.Position - poseA.Position, poseA.Orientation, poseB.Orientation, velocityA, velocityB,
                    speculativeMargin, speculativeMargin, new PairContinuation((int)continuation.Packed));
            }
            ////Pull the velocity information for all involved bodies. We will request a number of steps that will cover the motion path.
            ////number of substeps = min(maximum substep count, 1 + floor(estimated displacement / step length)), where
            ////estimated displacement = dt * (length(linear velocity A - linear velocity B) +
            ////                               maximum radius A * (length(angular velocity A) + maximum radius B * length(angular velocity B)) 
            ////Once we have a number of 
            ////We use the minimum step length of each contributing collidable. Treat non-substepping collidables as having a step length of infinity.
            //var stepLengthA = aCollidable.Continuity.UseSubstepping ? aCollidable.Continuity.MaximumStepLength : float.MaxValue;
            //var stepLengthB = bCollidable.Continuity.UseSubstepping ? bCollidable.Continuity.MaximumStepLength : float.MaxValue;
            //float stepLength = stepLengthA < stepLengthB ? stepLengthA : stepLengthB;
        }
    }
}