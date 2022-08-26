﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.ML.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.SearchSpace;
using static Microsoft.ML.DataOperationsCatalog;

namespace Microsoft.ML.AutoML
{
    public class AutoMLExperiment
    {
        internal const string PipelineSearchspaceName = "_pipeline_";
        private readonly AutoMLExperimentSettings _settings;
        private readonly MLContext _context;
        private double _bestError = double.MaxValue;
        private TrialResult _bestTrialResult = null;
        private readonly IServiceCollection _serviceCollection;
        private CancellationTokenSource _globalCancellationTokenSource;

        public AutoMLExperiment(MLContext context, AutoMLExperimentSettings settings)
        {
            _context = context;
            _settings = settings;

            if (_settings.Seed == null)
            {
                _settings.Seed = ((IHostEnvironmentInternal)_context.Model.GetEnvironment()).Seed;
            }

            if (_settings.SearchSpace == null)
            {
                _settings.SearchSpace = new SearchSpace.SearchSpace();
            }

            _serviceCollection = new ServiceCollection();
            InitializeServiceCollection();
        }

        private void InitializeServiceCollection()
        {
            _serviceCollection.TryAddTransient((provider) =>
            {
                var context = new MLContext(_settings.Seed);
                _globalCancellationTokenSource.Token.Register(() =>
                {
                    // only force-canceling running trials when there's completed trials.
                    // otherwise, wait for the current running trial to be completed.
                    if (_bestTrialResult != null)
                        context.CancelExecution();
                });

                return context;
            });

            this.SetPerformanceMonitor(100);
            _serviceCollection.TryAddSingleton(_settings);
            _serviceCollection.TryAddSingleton(((IChannelProvider)_context).Start(nameof(AutoMLExperiment)));
        }

        private void Initialize()
        {
            _globalCancellationTokenSource = new CancellationTokenSource();
        }

        internal IServiceCollection ServiceCollection { get => _serviceCollection; }

        public AutoMLExperiment SetTrainingTimeInSeconds(uint trainingTimeInSeconds)
        {
            _settings.MaxExperimentTimeInSeconds = trainingTimeInSeconds;
            return this;
        }

        public AutoMLExperiment SetIsMaximize(bool isMaximize)
        {
            _settings.IsMaximize = isMaximize;
            return this;
        }

        public AutoMLExperiment SetMaximumMemoryUsageInMegaByte(double value = double.MaxValue)
        {
            _settings.MaximumMemoryUsageInMegaByte = value;
            return this;
        }

        public AutoMLExperiment AddSearchSpace(string key, SearchSpace.SearchSpace searchSpace)
        {
            _settings.SearchSpace[key] = searchSpace;

            return this;
        }

        public AutoMLExperiment SetMonitor<TMonitor>(TMonitor monitor)
            where TMonitor : class, IMonitor
        {
            _serviceCollection.AddSingleton<IMonitor>(monitor);

            return this;
        }

        public AutoMLExperiment SetMonitor<TMonitor>()
            where TMonitor : class, IMonitor
        {
            _serviceCollection.AddSingleton<IMonitor, TMonitor>();

            return this;
        }

        public AutoMLExperiment SetMonitor<TMonitor>(Func<IServiceProvider, TMonitor> factory)
            where TMonitor : class, IMonitor
        {
            _serviceCollection.AddSingleton<IMonitor>(factory);

            return this;
        }

        public AutoMLExperiment SetTrialRunner<TTrialRunner>(TTrialRunner runner)
            where TTrialRunner : class, ITrialRunner
        {
            _serviceCollection.AddSingleton<ITrialRunner>(runner);

            return this;
        }

        public AutoMLExperiment SetTrialRunner<TTrialRunner>(Func<IServiceProvider, TTrialRunner> factory)
            where TTrialRunner : class, ITrialRunner
        {
            _serviceCollection.AddTransient<ITrialRunner>(factory);

            return this;
        }

        public AutoMLExperiment SetTrialRunner<TTrialRunner>()
            where TTrialRunner : class, ITrialRunner
        {
            _serviceCollection.AddTransient<ITrialRunner, TTrialRunner>();

            return this;
        }

        public AutoMLExperiment SetTuner<TTuner>(TTuner proposer)
            where TTuner : class, ITuner
        {
            return this.SetTuner((service) => proposer);
        }

        public AutoMLExperiment SetTuner<TTuner>(Func<IServiceProvider, TTuner> factory)
            where TTuner : class, ITuner
        {
            var descriptor = ServiceDescriptor.Singleton<ITuner>(factory);

            if (_serviceCollection.Contains(descriptor))
            {
                _serviceCollection.Replace(descriptor);
            }
            else
            {
                _serviceCollection.Add(descriptor);
            }

            return this;
        }

        public AutoMLExperiment SetTuner<TTuner>()
            where TTuner : class, ITuner
        {
            _serviceCollection.AddSingleton<ITuner, TTuner>();

            return this;
        }

        internal AutoMLExperiment SetPerformanceMonitor<TPerformanceMonitor>()
            where TPerformanceMonitor : class, IPerformanceMonitor
        {
            _serviceCollection.AddTransient<IPerformanceMonitor, TPerformanceMonitor>();

            return this;
        }

        internal AutoMLExperiment SetPerformanceMonitor<TPerformanceMonitor>(Func<IServiceProvider, TPerformanceMonitor> factory)
            where TPerformanceMonitor : class, IPerformanceMonitor
        {
            _serviceCollection.AddTransient<IPerformanceMonitor>(factory);

            return this;
        }

        /// <summary>
        /// Run experiment and return the best trial result synchronizely.
        /// </summary>
        public TrialResult Run()
        {
            return this.RunAsync().Result;
        }

        /// <summary>
        /// Run experiment and return the best trial result asynchronizely. The experiment returns the current best trial result if there's any trial completed when <paramref name="ct"/> get cancelled,
        /// and throws <see cref="TimeoutException"/> with message "Training time finished without completing a trial run" when no trial has completed.
        /// Another thing needs to notice is that this function won't immediately return after <paramref name="ct"/> get cancelled. Instead, it will call <see cref="MLContext.CancelExecution"/> to cancel all training process
        /// and wait all running trials get cancelled or completed.
        /// </summary>
        /// <returns></returns>
        public async Task<TrialResult> RunAsync(CancellationToken ct = default)
        {
            ValidateSettings();
            Initialize();
            _settings.CancellationToken = ct;
            _globalCancellationTokenSource.CancelAfter((int)_settings.MaxExperimentTimeInSeconds * 1000);
            _settings.CancellationToken.Register(() => _globalCancellationTokenSource.Cancel());
            var serviceProvider = _serviceCollection.BuildServiceProvider();
            var monitor = serviceProvider.GetService<IMonitor>();
            var logger = serviceProvider.GetRequiredService<IChannel>();
            var trialNum = 0;
            var tuner = serviceProvider.GetService<ITuner>();
            Contracts.Assert(tuner != null, "tuner can't be null");

            while (!_globalCancellationTokenSource.Token.IsCancellationRequested)
            {
                var setting = new TrialSettings()
                {
                    TrialId = trialNum++,
                    Parameter = Parameter.CreateNestedParameter(),
                };
                var parameter = tuner.Propose(setting);
                setting.Parameter = parameter;

                monitor?.ReportRunningTrial(setting);
                try
                {
                    using (var performanceMonitor = serviceProvider.GetService<IPerformanceMonitor>())
                    using (var runner = serviceProvider.GetRequiredService<ITrialRunner>())
                    {
                        var trialCancellationTokenSource = new CancellationTokenSource();
                        _globalCancellationTokenSource.Token.Register(() =>
                        {
                            // only force-canceling running trials when there's completed trials.
                            // otherwise, wait for the current running trial to be completed.
                            if (_bestTrialResult != null)
                                trialCancellationTokenSource.Cancel();
                        });

                        performanceMonitor.MemoryUsageInMegaByte += (o, m) =>
                        {
                            if (_settings.MaximumMemoryUsageInMegaByte is double d && m > d)
                            {
                                logger.Trace($"cancel current trial {setting.TrialId} because it uses {m} mb memory and the maximum memory usage is {d}");
                                trialCancellationTokenSource.Cancel();
                            }
                        };

                        performanceMonitor.Start();

                        var trialResult = await runner.RunAsync(setting, trialCancellationTokenSource.Token);

                        var peakCpu = performanceMonitor?.GetPeakCpuUsage();
                        var peakMemoryInMB = performanceMonitor?.GetPeakMemoryUsageInMegaByte();
                        trialResult.PeakCpu = peakCpu;
                        trialResult.PeakMemoryInMegaByte = peakMemoryInMB;

                        monitor?.ReportCompletedTrial(trialResult);
                        tuner.Update(trialResult);

                        var error = _settings.IsMaximize ? 1 - trialResult.Metric : trialResult.Metric;
                        if (error < _bestError)
                        {
                            _bestTrialResult = trialResult;
                            _bestError = error;
                            monitor?.ReportBestTrial(trialResult);
                        }
                    }
                }
                catch (OperationCanceledException ex) when (_globalCancellationTokenSource.IsCancellationRequested == false)
                {
                    monitor?.ReportFailTrial(setting, ex);
                    var result = new TrialResult
                    {
                        TrialSettings = setting,
                        Metric = _settings.IsMaximize ? double.MinValue : double.MaxValue,
                    };

                    tuner.Update(result);
                    continue;
                }
                catch (OperationCanceledException) when (_globalCancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    monitor?.ReportFailTrial(setting, ex);

                    if (!_globalCancellationTokenSource.IsCancellationRequested && _bestTrialResult == null)
                    {
                        // TODO
                        // it's questionable on whether to abort the entire training process
                        // for a single fail trial. We should make it an option and only exit
                        // when error is fatal (like schema mismatch).
                        throw;
                    }
                }
            }

            if (_bestTrialResult == null)
            {
                throw new TimeoutException("Training time finished without completing a trial run");
            }
            else
            {
                return await Task.FromResult(_bestTrialResult);
            }
        }

        private void ValidateSettings()
        {
            Contracts.Assert(_settings.MaxExperimentTimeInSeconds > 0, $"{nameof(ExperimentSettings.MaxExperimentTimeInSeconds)} must be larger than 0");
        }


        public class AutoMLExperimentSettings : ExperimentSettings
        {
            public int? Seed { get; set; }

            public SearchSpace.SearchSpace SearchSpace { get; set; }

            public bool IsMaximize { get; set; }
        }
    }
}
