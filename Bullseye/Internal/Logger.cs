#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable IDE0009 // Member access should be qualified.
namespace Bullseye.Internal
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using static System.Math;

    public class Logger
    {
        private readonly ConcurrentDictionary<string, TargetState> states = new ConcurrentDictionary<string, TargetState>();
        private readonly TextWriter writer;
        private readonly string prefix;
        private readonly bool skipDependencies;
        private readonly bool dryRun;
        private readonly bool parallel;
        private readonly Palette p;
        private readonly bool verbose;

        private int stateOrdinal;

        public Logger(TextWriter writer, string prefix, bool skipDependencies, bool dryRun, bool parallel, Palette palette, bool verbose)
        {
            this.writer = writer;
            this.prefix = prefix;
            this.skipDependencies = skipDependencies;
            this.dryRun = dryRun;
            this.parallel = parallel;
            this.p = palette;
            this.verbose = verbose;
        }

        public async Task Version()
        {
            if (this.verbose)
            {
                var version = typeof(TargetCollectionExtensions).Assembly.GetCustomAttributes(false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()
                    ?.InformationalVersion ?? "Unknown";

                await this.writer.WriteLineAsync(Message(p.Verbose, $"Bullseye version: {version}")).Tax();
            }
        }

        public Task Error(string message) => this.writer.WriteLineAsync(Message(p.Failed, message));

        public async Task Verbose(string message)
        {
            if (this.verbose)
            {
                await this.writer.WriteLineAsync(Message(p.Verbose, message)).Tax();
            }
        }

        public async Task Verbose(Stack<string> targets, string message)
        {
            if (this.verbose)
            {
                await this.writer.WriteLineAsync(Message(targets, p.Verbose, message)).Tax();
            }
        }

        public Task Starting(List<string> targets) =>
            this.writer.WriteLineAsync(Message(p.Default, $"Starting...", targets, null));

        public async Task Failed(List<string> targets, TimeSpan duration)
        {
            await this.WriteSummary().Tax();
            await this.writer.WriteLineAsync(Message(p.Failed, $"Failed!", targets, duration)).Tax();
        }

        public async Task Succeeded(List<string> targets, TimeSpan duration)
        {
            await this.WriteSummary().Tax();
            await this.writer.WriteLineAsync(Message(p.Succeeded, $"Succeeded.", targets, duration)).Tax();
        }

        public Task Succeeded(Target target)
        {
            var state = Intern(target);
            state.Status = TargetStatus.Succeeded;

            return this.writer.WriteLineAsync(Message(p.Succeeded, "Succeeded.", target));
        }

        public Task Starting(ActionTarget target)
        {
            Intern(target);

            return this.writer.WriteLineAsync(Message(p.Default, "Starting...", target, null));
        }

        public Task Error(ActionTarget target, Exception ex) =>
            this.writer.WriteLineAsync(Message(p.Failed, ex.ToString(), target));

        public Task Failed(ActionTarget target, Exception ex, TimeSpan duration)
        {
            var state = Intern(target);
            state.Status = TargetStatus.Failed;
            state.Duration = duration;

            return this.writer.WriteLineAsync(Message(p.Failed, $"Failed! {ex.Message}", target, duration));
        }

        public Task Succeeded(ActionTarget target, TimeSpan duration)
        {
            var state = Intern(target);
            state.Status = TargetStatus.Succeeded;
            state.Duration = duration;

            return this.writer.WriteLineAsync(Message(p.Succeeded, "Succeeded.", target, duration));
        }

        public Task NoInputs<TInput>(ActionTarget<TInput> target)
        {
            Intern(target).Status = TargetStatus.NoInputs;

            return this.writer.WriteLineAsync(Message(p.Warning, "No inputs!", target, null));
        }

        public Task Starting<TInput>(ActionTarget<TInput> target)
        {
            Intern(target);

            return this.writer.WriteLineAsync(Message(p.Default, "Starting...", target, null));
        }

        public Task Failed<TInput>(ActionTarget<TInput> target, TimeSpan duration)
        {
            var state = Intern(target);
            state.Status = TargetStatus.Failed;
            state.Duration = duration;

            return this.writer.WriteLineAsync(Message(p.Failed, $"Failed!", target, duration));
        }

        public Task Succeeded<TInput>(ActionTarget<TInput> target, TimeSpan duration)
        {
            var state = Intern(target);
            state.Status = TargetStatus.Succeeded;
            state.Duration = duration;

            return this.writer.WriteLineAsync(Message(p.Succeeded, "Succeeded.", target, duration));
        }

        public Task Starting<TInput>(ActionTarget<TInput> target, TInput input, Guid inputId)
        {
            var state = Intern(target, inputId);
            state.Input = input;

            return this.writer.WriteLineAsync(MessageWithInput(p.Default, "Starting...", target, input, null));
        }

        public Task Error<TInput>(ActionTarget<TInput> target, TInput input, Exception ex) =>
            this.writer.WriteLineAsync(MessageWithInput(p.Failed, ex.ToString(), target, input));

        public Task Failed<TInput>(ActionTarget<TInput> target, TInput input, Exception ex, TimeSpan duration, Guid inputId)
        {
            var state = Intern(target, inputId);
            state.Input = input;
            state.Status = InputStatus.Failed;
            state.Duration = duration;

            return this.writer.WriteLineAsync(MessageWithInput(p.Failed, $"Failed! {ex.Message}", target, input, duration));
        }

        public Task Succeeded<TInput>(ActionTarget<TInput> target, TInput input, TimeSpan duration, Guid inputId)
        {
            var state = Intern(target, inputId);
            state.Input = input;
            state.Status = InputStatus.Succeeded;
            state.Duration = duration;

            return this.writer.WriteLineAsync(MessageWithInput(p.Succeeded, "Succeeded.", target, input, duration));
        }

        private TargetState Intern(Target target) => this.states.GetOrAdd(target.Name, key => new TargetState(Interlocked.Increment(ref this.stateOrdinal)));

        private InputState Intern(Target target, Guid inputId) =>
            Intern(target).InputStates.GetOrAdd(inputId, key => new InputState(Interlocked.Increment(ref this.stateOrdinal)));

        private async Task WriteSummary()
        {
            // whitespace (e.g. can change to '·' for debugging)
            var ws = ' ';

            var totalDuration = states.Aggregate(
                TimeSpan.Zero,
                (total, state) =>
                    total +
                    (state.Value.Duration ?? state.Value.InputStates.Values.Aggregate(TimeSpan.Zero, (inputTotal, input) => inputTotal + input.Duration)));

            var rows = new List<SummaryRow> { new SummaryRow { TargetOrInput = $"{p.Default}Target{p.Reset}", Outcome = $"{p.Default}Outcome{p.Reset}", Duration = $"{p.Default}Duration{p.Reset}", Percentage = "" } };

            foreach (var state in states.OrderBy(state => state.Value.Ordinal))
            {
                var target = $"{p.Target}{state.Key}{p.Reset}";

                var outcome = state.Value.Status == TargetStatus.Failed
                    ? $"{p.Failed}Failed!{p.Reset}"
                    : state.Value.Status == TargetStatus.NoInputs
                        ? $"{p.Warning}No inputs!{p.Reset}"
                        : $"{p.Succeeded}Succeeded{p.Reset}";

                var duration = $"{p.Timing}{state.Value.Duration.Humanize(true)}{p.Reset}";

                var percentage = state.Value.Duration.HasValue && totalDuration > TimeSpan.Zero
                    ? $"{p.Timing}{100 * state.Value.Duration.Value.TotalMilliseconds / totalDuration.TotalMilliseconds:N1}%{p.Reset}"
                    : "";

                rows.Add(new SummaryRow { TargetOrInput = target, Outcome = outcome, Duration = duration, Percentage = percentage });

                var index = 0;

                foreach (var inputState in state.Value.InputStates.Values.OrderBy(inputState => inputState.Ordinal))
                {
                    var input = $"{ws}{ws}{p.Input}{inputState.Input}{p.Reset}";

                    var inputOutcome = inputState.Status == InputStatus.Failed ? $"{p.Failed}Failed!{p.Reset}" : $"{p.Succeeded}Succeeded{p.Reset}";

                    var inputDuration = $"{(index < state.Value.InputStates.Count - 1 ? p.TreeFork : p.TreeCorner)}{p.Timing}{inputState.Duration.Humanize(true)}{p.Reset}";

                    var inputPercentage = totalDuration > TimeSpan.Zero
                        ? $"{(index < state.Value.InputStates.Count - 1 ? p.TreeFork : p.TreeCorner)}{p.Timing}{100 * inputState.Duration.TotalMilliseconds / totalDuration.TotalMilliseconds:N1}%{p.Reset}"
                        : "";

                    rows.Add(new SummaryRow { TargetOrInput = input, Outcome = inputOutcome, Duration = inputDuration, Percentage = inputPercentage });

                    ++index;
                }
            }

            // target or input column width
            var tarW = rows.Max(row => Palette.StripColours(row.TargetOrInput).Length);

            // outcome column width
            var outW = rows.Max(row => Palette.StripColours(row.Outcome).Length);

            // duration column width
            var durW = rows.Count > 1 ? rows.Skip(1).Max(row => Palette.StripColours(row.Duration).Length) : 0;

            // percentage column width
            var perW = rows.Max(row => Palette.StripColours(row.Percentage).Length);

            // timing column width (duration and percentage)
            var timW = Max(Palette.StripColours(rows[0].Duration).Length, durW + 2 + perW);

            // expand percentage column width to ensure time and percentage are as wide as duration
            perW = Max(timW - durW - 2, perW);

            // summary start separator
            await this.writer.WriteLineAsync($"{GetPrefix()}{p.Default}{"".Prp(tarW + 2 + outW + 2 + timW, p.Dash)}{p.Reset}").Tax();

            // header
            await this.writer.WriteLineAsync($"{GetPrefix()}{rows[0].TargetOrInput.Prp(tarW, ws)}{ws}{ws}{rows[0].Outcome.Prp(outW, ws)}{ws}{ws}{rows[0].Duration.Prp(timW, ws)}").Tax();

            // header separator
            await this.writer.WriteLineAsync($"{GetPrefix()}{p.Default}{"".Prp(tarW, p.Dash)}{p.Reset}{ws}{ws}{p.Default}{"".Prp(outW, p.Dash)}{p.Reset}{ws}{ws}{p.Default}{"".Prp(timW, p.Dash)}{p.Reset}").Tax();

            // targets
            foreach (var row in rows.Skip(1))
            {
                await this.writer.WriteLineAsync($"{GetPrefix()}{row.TargetOrInput.Prp(tarW, ws)}{p.Reset}{ws}{ws}{row.Outcome.Prp(outW, ws)}{p.Reset}{ws}{ws}{row.Duration.Prp(durW, ws)}{p.Reset}{ws}{ws}{row.Percentage.Prp(perW, ws)}{p.Reset}").Tax();
            }

            // summary end separator
            await this.writer.WriteLineAsync($"{GetPrefix()}{p.Default}{"".Prp(tarW + 2 + outW + 2 + timW, p.Dash)}{p.Reset}").Tax();
        }

        private string Message(string color, string text) => $"{GetPrefix()}{color}{text}{p.Reset}";

        private string Message(Stack<string> targets, string color, string text) => $"{GetPrefix(targets)}{color}{text}{p.Reset}";

        private string Message(string color, string text, List<string> targets, TimeSpan? duration) =>
            $"{GetPrefix()}{color}{text}{p.Reset} {p.Target}({targets.Spaced()}){p.Reset}{GetSuffix(false, duration)}{p.Reset}";

        private string Message(string color, string text, Target target) =>
            $"{GetPrefix(target)}{color}{text}{p.Reset}";

        private string Message(string color, string text, Target target, TimeSpan? duration) =>
            $"{GetPrefix(target)}{color}{text}{p.Reset}{GetSuffix(true, duration)}{p.Reset}";

        private string MessageWithInput<TInput>(string color, string text, Target target, TInput input) =>
            $"{GetPrefix(target, input)}{color}{text}{p.Reset}";

        private string MessageWithInput<TInput>(string color, string text, Target target, TInput input, TimeSpan? duration) =>
            $"{GetPrefix(target, input)}{color}{text}{p.Reset}{GetSuffix(true, duration)}{p.Reset}";

        private string GetPrefix() =>
            $"{p.Prefix}{prefix}:{p.Reset} ";

        private string GetPrefix(Stack<string> targets) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{string.Join($"{p.Default}/{p.Target}", targets.Reverse())}{p.Default}:{p.Reset} ";

        private string GetPrefix(Target target) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{target.Name}{p.Default}:{p.Reset} ";

        private string GetPrefix<TInput>(Target target, TInput input) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{target.Name}{p.Default}/{p.Input}{input}{p.Default}:{p.Reset} ";

        private string GetSuffix(bool specific, TimeSpan? duration) =>
            (!specific && this.dryRun ? $" {p.Option}(dry run){p.Reset}" : "") +
                (!specific && this.parallel ? $" {p.Option}(parallel){p.Reset}" : "") +
                (!specific && this.skipDependencies ? $" {p.Option}(skip dependencies){p.Reset}" : "") +
                (!this.dryRun && duration.HasValue ? $" {p.Timing}({duration.Humanize()}){p.Reset}" : "");

        private class TargetState
        {
            public TargetState(int ordinal) => this.Ordinal = ordinal;

            public int Ordinal { get; }

            public TargetStatus Status { get; set; }

            public TimeSpan? Duration { get; set; }

            public ConcurrentDictionary<Guid, InputState> InputStates { get; } = new ConcurrentDictionary<Guid, InputState>();
        }

        private class InputState
        {
            public InputState(int ordinal) => this.Ordinal = ordinal;

            public int Ordinal { get; }

            public object Input { get; set; }

            public InputStatus Status { get; set; }

            public TimeSpan Duration { get; set; }
        }

        private class SummaryRow
        {
            public string TargetOrInput { get; set; }

            public string Outcome { get; set; }

            public string Duration { get; set; }

            public string Percentage { get; set; }
        }

        private enum TargetStatus
        {
            NoInputs,
            Failed,
            Succeeded,
        }

        private enum InputStatus
        {
            Failed,
            Succeeded,
        }
    }
}
