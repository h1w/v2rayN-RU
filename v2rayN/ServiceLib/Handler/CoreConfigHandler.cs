namespace ServiceLib.Handler;

/// <summary>
/// Core configuration file processing class
/// </summary>
public static class CoreConfigHandler
{
    private static readonly string _tag = "CoreConfigHandler";

    public static async Task<RetResult> GenerateClientConfig(CoreConfigContext context, string? fileName)
    {
        var config = AppManager.Instance.Config;
        var result = new RetResult();
        var node = context.Node;

        if (node.ConfigType == EConfigType.Custom)
        {
            result = node.CoreType switch
            {
                ECoreType.mihomo => await new CoreConfigClashService(config).GenerateClientCustomConfig(node, fileName),
                _ => await GenerateClientCustomConfig(context, fileName)
            };
        }
        else if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        }
        if (result.Success != true)
        {
            return result;
        }
        if (fileName.IsNotEmpty() && result.Data != null)
        {
            await File.WriteAllTextAsync(fileName, result.Data.ToString());
        }

        return result;
    }

    private static async Task<RetResult> GenerateClientCustomConfig(CoreConfigContext context, string? fileName)
    {
        var ret = new RetResult();
        try
        {
            var node = context.Node;
            if (node == null || fileName is null)
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            var addressFileName = node.Address;
            if (!File.Exists(addressFileName))
            {
                addressFileName = Utils.GetConfigPath(addressFileName);
            }
            if (!File.Exists(addressFileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            var rawJson = await File.ReadAllTextAsync(addressFileName);
            var coreType = AppManager.Instance.GetCoreType(node, EConfigType.Custom);

            // Compose ВСЕГДА получает исходный rawJson нетронутым. При
            // EnableCustomRuleEditing==true unified-сборка внутри Compose
            // (CustomConfigComposer.BuildUnifiedRules) сама делает reorder+filter
            // JSON-правил по CustomRuleState — ordinal'ы токенов считаются от
            // файла. Если бы здесь сначала прогонялся ApplyCustomRuleState, а
            // потом этот уже перестроенный JSON шёл в Compose, ordinal'ы токенов
            // и позиции в предобработанном массиве разъехались бы — правила
            // задваивались/терялись бы (double-processing). Поэтому
            // ApplyCustomRuleState здесь больше не вызывается.
            var composed = CustomConfigComposer.Compose(rawJson, coreType, context);

            if (composed.Json.IsNotEmpty())
            {
                ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
                ret.Success = true;
                ret.Data = composed.Json;
                WarnAboutCustomRoutingLimits(composed);
                return ret;
            }

            // Фолбэк: JSON непригоден для слияния (Compose вернул null — нет
            // outbounds/proxy-выхода и т.п.), unified-сборке в этом случае
            // нечем оперировать (нет главного proxy-тега для ремапа). Чтобы
            // правки пользователя (вкл/выкл/порядок JSON-правил) не терялись
            // молча под дословной копией исходника, для фолбэк-записи отдельно
            // пересобираем rawJson через ApplyCustomRuleState — но только по
            // JSON-токенам (LocalId == null): без outbounds локальным правилам
            // всё равно некуда маршрутизировать, а сам ApplyCustomRuleState не
            // умеет работать со смешанными (LocalId!=null) токенами.
            var ruleStateApplied = false;
            if (context.AppConfig.UiItem.EnableCustomRuleEditing)
            {
                var ruleState = JsonUtils.Deserialize<List<CustomRuleStateItem>>(node.CustomRuleState)
                    ?.Where(t => t.LocalId == null).ToList();
                if (ruleState?.Count > 0)
                {
                    rawJson = CustomConfigComposer.ApplyCustomRuleState(rawJson, coreType, ruleState);
                    ruleStateApplied = true;
                }
            }

            if (File.Exists(fileName))
            {
                File.SetAttributes(fileName, FileAttributes.Normal);
                File.Delete(fileName);
            }
            if (ruleStateApplied)
            {
                await File.WriteAllTextAsync(fileName, rawJson);
            }
            else
            {
                File.Copy(addressFileName, fileName);
            }
            File.SetAttributes(fileName, FileAttributes.Normal);

            if (!File.Exists(fileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    /// <summary>
    /// Сообщает о ситуациях, в которых локальные правила не сработают, вместо того
    /// чтобы молча отдать пользователю конфиг, ведущий себя не так, как показывает UI.
    /// </summary>
    private static void WarnAboutCustomRoutingLimits(CustomComposeResult composed)
    {
        if (composed.CatchAllDetected)
        {
            NoticeManager.Instance.SendMessageAndEnqueue(ResUI.CustomJsonCatchAllRuleWarning);
        }
        if (composed.UnsupportedCustomTargets.Count > 0)
        {
            NoticeManager.Instance.SendMessageAndEnqueue(
                string.Format(ResUI.CustomJsonRuleTargetUnsupported,
                    string.Join(", ", composed.UnsupportedCustomTargets.Distinct())));
        }
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, string fileName, List<ServerTestItem> selecteds, ECoreType coreType)
    {
        var result = new RetResult();
        var dummyNode = new ProfileItem
        {
            CoreType = coreType
        };
        var builderResult = await CoreConfigContextBuilder.Build(config, dummyNode);
        var context = builderResult.Context;
        foreach (var testItem in selecteds)
        {
            var node = testItem.Profile;
            var (actNode, _) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
            if (node.IndexId == actNode.IndexId)
            {
                continue;
            }
            context.ServerTestItemMap[node.IndexId] = actNode.IndexId;
        }
        if (coreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        else if (coreType == ECoreType.Xray)
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        if (result.Success != true)
        {
            return result;
        }
        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, CoreConfigContext context, ServerTestItem testItem, string fileName)
    {
        var result = new RetResult();
        var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);
        var port = Utils.GetFreePort(initPort + testItem.QueueNum);
        testItem.Port = port;

        if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(port);
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
        }
        if (result.Success != true)
        {
            return result;
        }

        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }
}
