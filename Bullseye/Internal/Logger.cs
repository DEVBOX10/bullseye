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
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, InputState>> inputStates = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, InputState>>();
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
            this.writer.WriteLineAsync(Message(GetText(TargetStatus.Starting), targets, null));

        public async Task Failed(List<string> targets, TimeSpan duration)
        {
            await this.WriteSummary().Tax();
            await this.writer.WriteLineAsync(Message(GetText(TargetStatus.Failed), targets, duration)).Tax();
        }

        public async Task Succeeded(List<string> targets, TimeSpan duration)
        {
            await this.WriteSummary().Tax();
            await this.writer.WriteLineAsync(Message(GetText(TargetStatus.Succeeded), targets, duration)).Tax();
        }

        public Task Succeeded(Target target) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Succeeded)));

        public Task Starting(ActionTarget target) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Starting)));

        public Task Error(ActionTarget target, Exception ex) =>
            this.writer.WriteLineAsync(Message(target, ex));

        public Task Failed(ActionTarget target, Exception ex, TimeSpan duration) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Failed, ex, duration)));

        public Task Succeeded(ActionTarget target, TimeSpan duration) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Succeeded, duration: duration)));

        public Task NoInputs<TInput>(ActionTarget<TInput> target) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.NoInputs)));

        public Task Starting<TInput>(ActionTarget<TInput> target) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Starting)));

        public Task Failed<TInput>(ActionTarget<TInput> target, TimeSpan duration) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Failed, duration: duration)));

        public Task Succeeded<TInput>(ActionTarget<TInput> target, TimeSpan duration) =>
            this.writer.WriteLineAsync(Message(Record(target, TargetStatus.Succeeded, duration: duration)));

        public Task Starting<TInput>(ActionTarget<TInput> target, TInput input, Guid inputId) =>
            this.writer.WriteLineAsync(Message(Record(target, inputId, input, InputStatus.Starting)));

        public Task Error<TInput>(ActionTarget<TInput> target, TInput input, Exception ex) =>
            this.writer.WriteLineAsync(Message(target, input, ex));

        public Task Failed<TInput>(ActionTarget<TInput> target, TInput input, Exception ex, TimeSpan duration, Guid inputId) =>
            this.writer.WriteLineAsync(Message(Record(target, inputId, input, InputStatus.Failed, ex, duration)));

        public Task Succeeded<TInput>(ActionTarget<TInput> target, TInput input, TimeSpan duration, Guid inputId) =>
            this.writer.WriteLineAsync(Message(Record(target, inputId, input, InputStatus.Succeeded, duration: duration)));

        private TargetState Record(Target target, TargetStatus status, Exception ex = null, TimeSpan? duration = null) =>
            this.states.AddOrUpdate(
                target.Name,
                name => new TargetState(name, status, ex, duration, Interlocked.Increment(ref this.stateOrdinal)),
                (name, current) => new TargetState(name, status, ex, duration, current.Ordinal));

        private InputState Record(Target target, Guid inputId, object input, InputStatus status, Exception ex = null, TimeSpan? duration = null) =>
            InternInputStates(target.Name).AddOrUpdate(
                inputId,
                _ => new InputState(target.Name, input, status, ex, duration, Interlocked.Increment(ref this.stateOrdinal)),
                (_, current) => new InputState(target.Name, input, status, ex, duration, current.Ordinal));

        private ConcurrentDictionary<Guid, InputState> InternInputStates(string target) =>
            inputStates.GetOrAdd(target, _ => new ConcurrentDictionary<Guid, InputState>());

        private async Task WriteSummary()
        {
            // whitespace (e.g. can change to '·' for debugging)
            var ws = ' ';

            var totalDuration = states.Values.Aggregate(
                TimeSpan.Zero,
                (total, state) =>
                    total +
                    (state.Duration ?? InternInputStates(state.Name).Values.Aggregate(TimeSpan.Zero, (inputTotal, input) => inputTotal + input.Duration ?? TimeSpan.Zero)));

            var rows = new List<SummaryRow> { new SummaryRow { TargetOrInput = $"{p.Default}Target{p.Reset}", Outcome = $"{p.Default}Outcome{p.Reset}", Duration = $"{p.Default}Duration{p.Reset}", Percentage = "" } };

            foreach (var state in states.Values.OrderBy(state => state.Ordinal))
            {
                var target = $"{p.Target}{state.Name}{p.Reset}";

                var outcome = GetText(state.Status);

                var duration = $"{p.Timing}{state.Duration.Humanize(true)}{p.Reset}";

                var percentage = state.Duration.HasValue && totalDuration > TimeSpan.Zero
                    ? $"{p.Timing}{100 * state.Duration.Value.TotalMilliseconds / totalDuration.TotalMilliseconds:N1}%{p.Reset}"
                    : "";

                rows.Add(new SummaryRow { TargetOrInput = target, Outcome = outcome, Duration = duration, Percentage = percentage });

                var index = 0;

                foreach (var inputState in InternInputStates(state.Name).Values.OrderBy(inputState => inputState.Ordinal))
                {
                    var input = $"{ws}{ws}{p.Input}{inputState.Input}{p.Reset}";

                    var inputOutcome = GetText(inputState.Status);

                    var inputDuration = $"{(index < InternInputStates(state.Name).Count - 1 ? p.TreeFork : p.TreeCorner)}{p.Timing}{inputState.Duration.Humanize(true)}{p.Reset}";

                    var inputPercentage = inputState.Duration.HasValue && totalDuration > TimeSpan.Zero
                        ? $"{(index < InternInputStates(state.Name).Count - 1 ? p.TreeFork : p.TreeCorner)}{p.Timing}{100 * inputState.Duration.Value.TotalMilliseconds / totalDuration.TotalMilliseconds:N1}%{p.Reset}"
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
            await this.writer.WriteLineAsync($"{GetPrefix()}{rows[0].TargetOrInput.Prp(tarW, ws)}{ws}{ws}{rows[0].Outcome.Prp(outW, ws)}{ws}{ws}{rows[0].Duration.Prp(timW, ws)}{p.Reset}").Tax();

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

        private string Message(string color, string text) =>
            $"{GetPrefix()}{color}{text}{p.Reset}";

        private string Message(IEnumerable<string> targets, string color, string text) =>
            $"{GetPrefix(targets)}{color}{text}{p.Reset}";

        private string Message(string text, List<string> targets, TimeSpan? duration) =>
            $"{GetPrefix()}{text} {p.Target}({targets.Spaced()}){p.Reset}{GetSuffix(false, duration)}";

        private string Message(TargetState state) =>
            $"{GetPrefix(state.Name)}{GetText(state.Status, state.Exception)}{GetSuffix(true, state.Duration)}";

        private string Message(InputState state) =>
            $"{GetPrefix(state.TargetName, state.Input)}{GetText(state.Status, state.Exception)}{GetSuffix(true, state.Duration)}";

        private string Message(Target target, Exception ex) =>
            $"{GetPrefix(target.Name)}{p.Failed}{ex}{p.Reset}";

        private string Message(Target target, object input, Exception ex) =>
            $"{GetPrefix(target.Name, input)}{p.Failed}{ex}{p.Reset}";

        private string GetPrefix() =>
            $"{p.Prefix}{prefix}:{p.Reset} ";

        private string GetPrefix(IEnumerable<string> targets) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{string.Join($"{p.Default}/{p.Target}", targets.Reverse())}{p.Default}:{p.Reset} ";

        private string GetPrefix(string target) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{target}{p.Default}:{p.Reset} ";

        private string GetPrefix(string target, object input) =>
            $"{p.Prefix}{prefix}:{p.Reset} {p.Target}{target}{p.Default}/{p.Input}{input}{p.Default}:{p.Reset} ";

        private string GetText(TargetStatus? state, Exception ex = null)
        {
            switch (state)
            {
                case TargetStatus.NoInputs:
                    return $"{p.Warning}No inputs!{p.Reset}";
                case TargetStatus.Starting:
                    return $"{p.Default}Starting...{p.Reset}";
                case TargetStatus.Failed:
                    return $"{p.Failed}Failed!{(ex != null ? $" {ex.Message}" : "")}{p.Reset}";
                case TargetStatus.Succeeded:
                    return $"{p.Succeeded}Succeeded{p.Reset}";
                default:
                    return default;
            }
        }

        private string GetText(InputStatus state, Exception ex = null)
        {
            switch (state)
            {
                case InputStatus.Starting:
                    return $"{p.Default}Starting...{p.Reset}";
                case InputStatus.Failed:
                    return $"{p.Failed}Failed!{(ex != null ? $" {ex.Message}" : "")}{p.Reset}";
                case InputStatus.Succeeded:
                    return $"{p.Succeeded}Succeeded{p.Reset}";
                default:
                    return default;
            }
        }

        private string GetSuffix(bool specific, TimeSpan? duration) =>
            (!specific && this.dryRun ? $" {p.Option}(dry run){p.Reset}" : "") +
                (!specific && this.parallel ? $" {p.Option}(parallel){p.Reset}" : "") +
                (!specific && this.skipDependencies ? $" {p.Option}(skip dependencies){p.Reset}" : "") +
                (!this.dryRun && duration.HasValue ? $" {p.Timing}({duration.Humanize()}){p.Reset}" : "");

        private class TargetState
        {
            public TargetState(string name, TargetStatus? status, Exception exception, TimeSpan? duration, int ordinal)
            {
                this.Name = name;
                this.Status = status;
                this.Exception = exception;
                this.Duration = duration;
                this.Ordinal = ordinal;
            }

            public string Name { get; }

            public TargetStatus? Status { get; }

            public Exception Exception { get; }

            public TimeSpan? Duration { get; }

            public int Ordinal { get; }
        }

        private class InputState
        {
            public InputState(string targetName, object input, InputStatus status, Exception exception, TimeSpan? duration, int ordinal)
            {
                this.TargetName = targetName;
                this.Input = input;
                this.Status = status;
                this.Exception = exception;
                this.Duration = duration;
                this.Ordinal = ordinal;
            }

            public string TargetName { get; }

            public object Input { get; }

            public InputStatus Status { get; }

            public Exception Exception { get; }

            public TimeSpan? Duration { get; }

            public int Ordinal { get; }
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
            Starting,
            Failed,
            Succeeded,
        }

        private enum InputStatus
        {
            Starting,
            Failed,
            Succeeded,
        }
    }
}
