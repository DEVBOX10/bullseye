using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Bullseye.Internal
{
    public class ActionTarget<TInput> : Target, IHaveInputs
    {
        private readonly IEnumerable<TInput> inputs;
        private readonly Func<TInput, Task> action;

        public ActionTarget(string name, string description, IEnumerable<string> dependencies, IEnumerable<TInput> inputs, Func<TInput, Task> action)
            : base(name, description, dependencies)
        {
            this.inputs = inputs;
            this.action = action;
        }

        public IEnumerable<object?> Inputs
        {
            get
            {
                foreach (var input in this.inputs)
                {
                    yield return input;
                }
            }
        }

        public override async Task RunAsync(bool dryRun, bool parallel, Output output, Func<Exception, bool> messageOnly, IReadOnlyCollection<Target> dependencyPath)
        {
            var inputsList = this.inputs.ToList();

            if (inputsList.Count == 0)
            {
                await output.NoInputs(this, dependencyPath).Tax();
                return;
            }

            await output.BeginGroup(this).Tax();

            try
            {
                await output.Starting(this, dependencyPath).Tax();

                try
                {
                    if (parallel)
                    {
                        var tasks = inputsList.Select(input => this.RunAsync(input, dryRun, output, messageOnly, dependencyPath)).ToList();

                        await Task.WhenAll(tasks).Tax();
                    }
                    else
                    {
                        foreach (var input in inputsList)
                        {
                            await this.RunAsync(input, dryRun, output, messageOnly, dependencyPath).Tax();
                        }
                    }
                }
                catch (Exception)
                {
                    await output.Failed(this, dependencyPath).Tax();
                    throw;
                }

                await output.Succeeded(this, dependencyPath).Tax();
            }
            finally
            {
                await output.EndGroup().Tax();
            }
        }

        private async Task RunAsync(TInput input, bool dryRun, Output output, Func<Exception, bool> messageOnly, IReadOnlyCollection<Target> dependencyPath)
        {
            var id = Guid.NewGuid();

            await output.Starting(this, input, id, dependencyPath).Tax();

            var stopWatch = new Stopwatch();

            if (!dryRun)
            {
                await this.RunAsync(input, output, messageOnly, dependencyPath, id, stopWatch).Tax();
            }

            await output.Succeeded(this, input, id, dependencyPath, stopWatch.Elapsed).Tax();
        }

        private async Task RunAsync(TInput input, Output output, Func<Exception, bool> messageOnly, IReadOnlyCollection<Target> dependencyPath, Guid id, Stopwatch stopWatch)
        {
            stopWatch.Start();

            try
            {
                await this.action(input).Tax();
            }
            catch (Exception ex)
            {
                var duration = stopWatch.Elapsed;

                if (!messageOnly(ex))
                {
                    await output.Error(this, input, ex).Tax();
                }

                await output.Failed(this, input, ex, duration, id, dependencyPath).Tax();

                throw new TargetFailedException($"Target '{this.Name}' failed with input '{input}'.", ex);
            }
        }
    }
}
