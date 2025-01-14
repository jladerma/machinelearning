﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.GenAI.Core;
using Microsoft.ML.GenAI.Phi.Module;
using TorchSharp;
using TorchSharp.Modules;
using TorchSharp.PyBridge;
using static TorchSharp.torch;

namespace Microsoft.ML.GenAI.Phi;

public class Phi3ForCasualLM : nn.Module<CasualLMModelInput, CasualLMModelOutput>
{
    private readonly Phi3Config _config;

#pragma warning disable MSML_PrivateFieldName // Private field name not in: _camelCase format
    private readonly Phi3Model model;
    private readonly GenAILinear lm_head;
#pragma warning restore MSML_PrivateFieldName // Private field name not in: _camelCase format

    public Phi3ForCasualLM(Phi3Config config)
        : base(nameof(Phi3ForCasualLM))
    {
        this._config = config;
        this.model = new Phi3Model(config);
        this.lm_head = new GenAILinear(config.HiddenSize, config.VocabSize, dtype: config.DType, hasBias: false);

        this.RegisterComponents();
    }

#pragma warning disable MSML_GeneralName // This name should be PascalCased
    public override CasualLMModelOutput forward(CasualLMModelInput input)
#pragma warning restore MSML_GeneralName // This name should be PascalCased
    {
        var outputs = this.model.forward(input);
        var logits = this.lm_head.forward(outputs.LastHiddenState);
        logits = logits.to_type(ScalarType.Float32);
        outputs.Logits = logits;

        return outputs;
    }

    public static Phi3ForCasualLM FromPretrained(
        string modelFolder,
        string configName = "config.json",
        string checkPointName = "model.safetensors.index.json",
        ScalarType torchDtype = ScalarType.BFloat16,
        string device = "cpu")
    {
        var config = Path.Join(modelFolder, configName);
        var modelConfig = JsonSerializer.Deserialize<Phi3Config>(File.ReadAllText(config)) ?? throw new ArgumentNullException(nameof(config));
        modelConfig.DType = torchDtype;
        var phi = new Phi3ForCasualLM(modelConfig);
        phi.LoadSafeTensors(modelFolder, checkPointName);
        phi = phi.to(device);
        phi.eval();

        return phi;
    }

    public void LoadSafeTensors(string modelFolder, string checkPointName = "model.safetensors.index.json")
    {
        this.load_checkpoint(path: modelFolder, checkpointName: checkPointName, strict: false, useTqdm: false);
    }
}
