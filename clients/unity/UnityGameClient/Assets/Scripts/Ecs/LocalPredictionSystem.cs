using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace AI003.Ecs
{
    /// <summary>
    /// Burst job that steps the local fighter at 20 Hz in integer millimetres,
    /// keeps history and reconciles against the newest server snapshot.
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(LocalFighterTag))]
    public partial struct LocalPredictionJob : IJobEntity
    {
        private const float StepSeconds = 0.05f;      // 20 Hz
        private const int MaxTicksAhead = 8;
        private const int HistoryCapacity = 64;
        private const int ReconcileThresholdMm = 50;

        public float DeltaTime;

        public void Execute(
            ref PredictionState prediction,
            in PredictionInput input,
            ref ServerSnapshot snapshot,
            ref FighterState fighter,
            DynamicBuffer<PredictionStep> steps,
            DynamicBuffer<PredictionHistoryElement> history,
            DynamicBuffer<SeparationBody> bodies)
        {
            if (snapshot.Pending)
            {
                ApplySnapshot(ref prediction, snapshot, steps, history);
                snapshot.Pending = false;
            }

            prediction.Accumulator = math.min(prediction.Accumulator + DeltaTime, 0.5f);
            int targetTick = snapshot.Tick > 0 ? snapshot.Tick + 3 : prediction.Tick + 1;
            int stepped = 0;

            while (prediction.Accumulator >= StepSeconds
                   && prediction.Tick < targetTick
                   && stepped < MaxTicksAhead)
            {
                prediction.Accumulator -= StepSeconds;
                prediction.Tick++;
                Step(ref prediction, input.DX, input.DZ);
                Separate(ref prediction, bodies);
                steps.Add(new PredictionStep
                {
                    Tick = prediction.Tick,
                    DX = input.DX,
                    DZ = input.DZ,
                    Attack = input.Attack
                });
                history.Add(new PredictionHistoryElement
                {
                    Tick = prediction.Tick,
                    PositionMm = prediction.PositionMm
                });
                stepped++;
            }

            Prune(steps, history, snapshot.Tick);

            fighter.Position.x = prediction.PositionMm.x / 1000f;
            fighter.Position.z = prediction.PositionMm.y / 1000f;
            fighter.Yaw = math.degrees(math.atan2(prediction.FacingX, prediction.FacingZ));
        }

        private static void ApplySnapshot(ref PredictionState prediction, in ServerSnapshot snapshot,
            DynamicBuffer<PredictionStep> steps, DynamicBuffer<PredictionHistoryElement> history)
        {
            int error = ReconcileThresholdMm + 1;
            for (int i = 0; i < history.Length; i++)
            {
                if (history[i].Tick != snapshot.Tick) continue;
                int2 delta = history[i].PositionMm - snapshot.PositionMm;
                error = math.abs(delta.x) + math.abs(delta.y);
                break;
            }

            if (error <= ReconcileThresholdMm)
            {
                return;
            }

            prediction.PositionMm = snapshot.PositionMm;
            prediction.Tick = snapshot.Tick;
            prediction.Accumulator = 0f;
            prediction.Corrections++;

            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Tick <= snapshot.Tick) continue;
                Step(ref prediction, steps[i].DX, steps[i].DZ);
                prediction.Tick = steps[i].Tick;
            }

            history.Clear();
            history.Add(new PredictionHistoryElement
            {
                Tick = prediction.Tick,
                PositionMm = prediction.PositionMm
            });
        }

        private static void Step(ref PredictionState state, int dx, int dz)
        {
            if (dx == 0 && dz == 0) return;

            int step = dx != 0 && dz != 0 ? state.DiagonalMm : state.SpeedMm;
            int2 next = state.PositionMm + new int2(dx * step, dz * step);
            next.x = math.clamp(next.x - state.CenterMm.x, -state.HalfExtentMm, state.HalfExtentMm) + state.CenterMm.x;
            next.y = math.clamp(next.y - state.CenterMm.y, -state.HalfExtentMm, state.HalfExtentMm) + state.CenterMm.y;

            state.PositionMm = next;
            state.FacingX = dx;
            state.FacingZ = dz;
        }

        /// <summary>
        /// Mirrors the server's invariant separation: push the local fighter away
        /// from every overlapping body by 20 mm along the dominant axis.
        /// </summary>
        private static void Separate(ref PredictionState state, DynamicBuffer<SeparationBody> bodies)
        {
            const int MinDistMm = 2 * 400;
            const int StepMm = 20;

            for (int i = 0; i < bodies.Length; i++)
            {
                if (!bodies[i].Alive) continue;

                int2 delta = state.PositionMm - bodies[i].PositionMm;
                int distSq = delta.x * delta.x + delta.y * delta.y;
                if (distSq >= MinDistMm * MinDistMm) continue;
                if (delta.x == 0 && delta.y == 0) delta.x = 1;

                if (math.abs(delta.x) >= math.abs(delta.y))
                {
                    state.PositionMm.x += delta.x > 0 ? StepMm : -StepMm;
                    state.PositionMm.x = math.clamp(state.PositionMm.x - state.CenterMm.x,
                        -state.HalfExtentMm, state.HalfExtentMm) + state.CenterMm.x;
                }
                else
                {
                    state.PositionMm.y += delta.y > 0 ? StepMm : -StepMm;
                    state.PositionMm.y = math.clamp(state.PositionMm.y - state.CenterMm.y,
                        -state.HalfExtentMm, state.HalfExtentMm) + state.CenterMm.y;
                }
            }
        }

        private static void Prune(DynamicBuffer<PredictionStep> steps,
            DynamicBuffer<PredictionHistoryElement> history, int serverTick)
        {
            for (int i = steps.Length - 1; i >= 0; i--)
            {
                if (steps[i].Tick <= serverTick && steps[i].Sent) steps.RemoveAt(i);
            }
            for (int i = history.Length - 1; i >= 0; i--)
            {
                if (history[i].Tick < serverTick) history.RemoveAt(i);
            }

            while (history.Length > HistoryCapacity) history.RemoveAt(0);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(FighterStateUpdateSystem))]
    public partial struct LocalPredictionSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = math.min(SystemAPI.Time.DeltaTime, 0.2f);
            if (deltaTime <= 0f) return;

            state.Dependency = new LocalPredictionJob
            {
                DeltaTime = deltaTime
            }.Schedule(state.Dependency);
        }
    }
}
