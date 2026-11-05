using Unity.Entities;
using Unity.Mathematics;

namespace AI003.Ecs
{
    /// <summary>
    /// Steps the local fighter at 20 Hz in integer millimetres (same rules as the
    /// Go server), keeps a short history for rollback and emits one input per tick
    /// for the network layer to send.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LocalPredictionSystem : ISystem
    {
        private const float StepSeconds = 0.05f;      // 20 Hz
        private const int MaxTicksAhead = 8;          // catch-up cap per frame
        private const int HistoryCapacity = 64;
        private const int ReconcileThresholdMm = 50;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PredictionState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = math.min(SystemAPI.Time.DeltaTime, 0.2f);
            if (deltaTime <= 0f) return;

            foreach (var (prediction, input, snapshot, fighter, steps, history) in SystemAPI.Query<
                         RefRW<PredictionState>,
                         RefRO<PredictionInput>,
                         RefRW<ServerSnapshot>,
                         RefRW<FighterState>,
                         DynamicBuffer<PredictionStep>,
                         DynamicBuffer<PredictionHistoryElement>>()
                         .WithAll<LocalFighterTag>())
            {
                ref PredictionState predictionState = ref prediction.ValueRW;
                ref ServerSnapshot serverSnapshot = ref snapshot.ValueRW;

                if (serverSnapshot.Pending)
                {
                    ApplySnapshot(ref predictionState, serverSnapshot, steps, history);
                    serverSnapshot.Pending = false;
                }

                predictionState.Accumulator = math.min(predictionState.Accumulator + deltaTime, 0.5f);
                int targetTick = serverSnapshot.Tick > 0 ? serverSnapshot.Tick + 3 : predictionState.Tick + 1;
                int stepped = 0;

                while (predictionState.Accumulator >= StepSeconds
                       && predictionState.Tick < targetTick
                       && stepped < MaxTicksAhead)
                {
                    predictionState.Accumulator -= StepSeconds;
                    predictionState.Tick++;
                    Step(ref predictionState, input.ValueRO.DX, input.ValueRO.DZ);
                    steps.Add(new PredictionStep
                    {
                        Tick = predictionState.Tick,
                        DX = input.ValueRO.DX,
                        DZ = input.ValueRO.DZ,
                        Attack = input.ValueRO.Attack
                    });
                    history.Add(new PredictionHistoryElement
                    {
                        Tick = predictionState.Tick,
                        PositionMm = predictionState.PositionMm
                    });
                    stepped++;
                }

                Prune(steps, history, serverSnapshot.Tick);

                // Drive the presentation entity from the predicted state.
                FighterState fighterState = fighter.ValueRW;
                fighterState.Position.x = predictionState.PositionMm.x / 1000f;
                fighterState.Position.z = predictionState.PositionMm.y / 1000f;
                fighterState.Yaw = math.degrees(math.atan2(predictionState.FacingX, predictionState.FacingZ));
                fighter.ValueRW = fighterState;
            }
        }

        /// <summary>Snaps to the server when off by more than the threshold, then replays pending input.</summary>
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

            // Replay inputs that were predicted after the snapshot tick.
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].Tick <= snapshot.Tick) continue;
                Step(ref prediction, steps[i].DX, steps[i].DZ);
                prediction.Tick = steps[i].Tick;
            }

            // Rebuild history around the reconciled state.
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
}
