namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private void GenRouting()
    {
        try
        {
            if (context.IsTunEnabled)
            {
                var tunRules = JsonUtils.Deserialize<List<RulesItem4Ray>>(EmbedUtils.GetEmbedText(Global.V2raySampleTunRules));
                if (tunRules != null)
                {
                    _coreConfig.routing.rules.AddRange(tunRules);
                }
                var lstDirectExe = BuildRoutingDirectExe();
                if (lstDirectExe.Count > 0)
                {
                    _coreConfig.routing.rules.Add(new()
                    {
                        port = "53",
                        process = lstDirectExe,
                        outboundTag = Global.DnsOutboundTag,
                    });
                    _coreConfig.routing.rules.Add(new()
                    {
                        process = lstDirectExe,
                        outboundTag = Global.DirectTag,
                    });
                }
                _coreConfig.routing.rules.Add(new()
                {
                    inboundTag = ["tun"],
                    port = "53",
                    outboundTag = Global.DnsOutboundTag,
                });
            }
            if (_coreConfig.routing?.rules != null)
            {
                _coreConfig.routing.domainStrategy = _config.RoutingBasicItem.DomainStrategy;

                var routing = context.RoutingItem;
                if (routing != null)
                {
                    if (routing.DomainStrategy.IsNotEmpty())
                    {
                        _coreConfig.routing.domainStrategy = routing.DomainStrategy;
                    }
                    var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
                    foreach (var item in rules)
                    {
                        if (!item.Enabled)
                        {
                            continue;
                        }

                        if (item.RuleType == ERuleType.DNS)
                        {
                            continue;
                        }

                        var item2 = JsonUtils.Deserialize<RulesItem4Ray>(JsonUtils.Serialize(item));
                        GenRoutingUserRule(item2);
                    }
                }
                ApplyBalancerTags(_coreConfig);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// Правила, нацеленные на группу с несколькими выходами, уходят на балансер:
    /// outboundTag очищается, взамен проставляется balancerTag. Общий пост-проход
    /// для GenRouting и BuildUserRoutingForCustom.
    /// </summary>
    private static void ApplyBalancerTags(V2rayConfig coreConfig)
    {
        var balancerTagList = coreConfig.routing.balancers?.Select(p => p.tag).ToList() ?? [];
        if (balancerTagList.Count == 0)
        {
            return;
        }
        foreach (var rulesItem in coreConfig.routing.rules.Where(r => balancerTagList.Contains(r.outboundTag + Global.BalancerTagSuffix)))
        {
            rulesItem.balancerTag = rulesItem.outboundTag + Global.BalancerTagSuffix;
            rulesItem.outboundTag = null;
        }
    }

    private void GenRoutingUserRule(RulesItem4Ray? userRule)
    {
        try
        {
            if (userRule == null)
            {
                return;
            }
            userRule.outboundTag = GenRoutingUserRuleOutbound(userRule.outboundTag ?? Global.ProxyTag);

            if (userRule.port.IsNullOrEmpty())
            {
                userRule.port = null;
            }
            if (userRule.network.IsNullOrEmpty())
            {
                userRule.network = null;
            }
            if (userRule.domain?.Count == 0)
            {
                userRule.domain = null;
            }
            if (userRule.ip?.Count == 0)
            {
                userRule.ip = null;
            }
            if (userRule.protocol?.Count == 0)
            {
                userRule.protocol = null;
            }
            if (userRule.inboundTag?.Count == 0)
            {
                userRule.inboundTag = null;
            }
            if (userRule.process?.Count == 0)
            {
                userRule.process = null;
            }

            var hasDomainIp = false;
            if (userRule.domain?.Count > 0)
            {
                var it = JsonUtils.DeepCopy(userRule);
                it.ip = null;
                it.process = null;
                it.type = "field";
                for (var k = it.domain.Count - 1; k >= 0; k--)
                {
                    if (it.domain[k].StartsWith('#'))
                    {
                        it.domain.RemoveAt(k);
                        continue;
                    }
                    it.domain[k] = it.domain[k].Replace(Global.RoutingRuleComma, ",");
                }
                _coreConfig.routing.rules.Add(it);
                hasDomainIp = true;
            }
            if (userRule.ip?.Count > 0)
            {
                var it = JsonUtils.DeepCopy(userRule);
                it.domain = null;
                it.process = null;
                it.type = "field";
                _coreConfig.routing.rules.Add(it);
                hasDomainIp = true;
            }
            if (userRule.process?.Count > 0)
            {
                var it = JsonUtils.DeepCopy(userRule);
                it.domain = null;
                it.ip = null;
                it.type = "field";
                _coreConfig.routing.rules.Add(it);
                hasDomainIp = true;
            }
            if (!hasDomainIp)
            {
                if (userRule.port.IsNotEmpty()
                    || userRule.protocol?.Count > 0
                    || userRule.inboundTag?.Count > 0
                    || userRule.network != null
                    )
                {
                    var it = JsonUtils.DeepCopy(userRule);
                    it.type = "field";
                    _coreConfig.routing.rules.Add(it);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private string GenRoutingUserRuleOutbound(string outboundTag)
    {
        if (Global.OutboundTags.Contains(outboundTag))
        {
            return outboundTag;
        }

        var node = context.AllProxiesMap.GetValueOrDefault($"remark:{outboundTag}");

        if (node == null
            || (!Global.XraySupportConfigType.Contains(node.ConfigType)
            && !node.ConfigType.IsGroupType()))
        {
            return Global.ProxyTag;
        }

        var tag = $"{node.IndexId}-{Global.ProxyTag}-{node.Remarks}";
        if (_coreConfig.outbounds.Any(p => p.tag.StartsWith(tag)))
        {
            return tag;
        }

        var proxyOutbounds = new CoreConfigV2rayService(context with { Node = node, }).BuildAllProxyOutbounds(tag);
        _coreConfig.outbounds.AddRange(proxyOutbounds);
        if (proxyOutbounds.Count(n => n.tag.StartsWith(tag)) > 1)
        {
            var multipleLoad = node.GetProtocolExtra().MultipleLoad ?? EMultipleLoad.LeastPing;
            GenObservatory(multipleLoad, tag);
            GenBalancer(multipleLoad, tag);
        }

        return tag;
    }

    private RulesItem4Ray BuildFinalRule()
    {
        var finalRule = new RulesItem4Ray()
        {
            type = "field",
            network = "tcp,udp",
            outboundTag = Global.ProxyTag,
        };
        var balancer =
            _coreConfig?.routing?.balancers?.FirstOrDefault(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix, null);
        var domainStrategy = _coreConfig.routing?.domainStrategy ?? Global.AsIs;
        if (balancer is not null)
        {
            finalRule.outboundTag = null;
            finalRule.balancerTag = balancer.tag;
        }
        if (domainStrategy == Global.IPIfNonMatch)
        {
            finalRule.network = null;
            finalRule.ip = ["0.0.0.0/0", "::/0"];
        }
        return finalRule;
    }

    private List<string> BuildRoutingDirectExe()
    {
        var directExeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allCoreInfo = CoreInfoManager.Instance.GetCoreInfo();

        foreach (var coreConfig in allCoreInfo)
        {
            if (!context.ProtectCoreTypeList.Contains(coreConfig.CoreType))
            {
                continue;
            }
            if (coreConfig.CoreType == ECoreType.v2rayN)
            {
                continue;
            }
            if (coreConfig.CoreExes == null)
            {
                continue;
            }
            if (coreConfig.CoreType == ECoreType.Xray)
            {
                directExeSet.Add("xray/");
                continue;
            }
            foreach (var baseExeName in coreConfig.CoreExes)
            {
                //directExeSet.Add(Utils.GetExeName(baseExeName));
                var exePath = CoreInfoManager.Instance.GetCoreExecFile(coreConfig, out _);
                if (!exePath.IsNullOrEmpty())
                {
                    directExeSet.Add(exePath);
                }
            }
        }
        //directExeSet.Add("xray/");
        directExeSet.Add("self/");

        return directExeSet.ToList();
    }

    /// <summary>
    /// Генерирует ТОЛЬКО пользовательские правила, в отрыве от оболочки приложения:
    /// без TUN-правил, без domainStrategy, без инбаундов и DNS. Нужен для вливания
    /// правил в пользовательский custom JSON, который остаётся основой конфига.
    /// </summary>
    public V2rayUserRouting BuildUserRoutingForCustom()
    {
        var fragment = new V2rayUserRouting();
        try
        {
            var template = EmbedUtils.GetEmbedText(Global.V2raySampleClient);
            var shell = JsonUtils.Deserialize<V2rayConfig>(template);
            if (shell == null)
            {
                return fragment;
            }
            _coreConfig = shell;

            // Выходы шаблона (direct/block) в конфиг пользователя не переносятся —
            // их наличие обеспечивается отдельно, по факту использования.
            var templateTags = _coreConfig.outbounds.Select(o => o.tag).ToHashSet(StringComparer.Ordinal);

            var routing = context.RoutingItem;
            if (routing == null)
            {
                return fragment;
            }
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
            foreach (var item in rules)
            {
                if (!item.Enabled || item.RuleType == ERuleType.DNS)
                {
                    continue;
                }
                var target = ResolveUnsupportedCustomTarget(item.OutboundTag);
                if (target != null)
                {
                    fragment.UnsupportedCustomTargets.Add(target);
                    continue;
                }
                var item2 = JsonUtils.Deserialize<RulesItem4Ray>(JsonUtils.Serialize(item));
                GenRoutingUserRule(item2);
            }

            // Тот же пост-проход, что и в GenRouting: правила на группы уходят на балансер.
            ApplyBalancerTags(_coreConfig);

            fragment.Rules = _coreConfig.routing.rules;
            fragment.ExtraOutbounds = _coreConfig.outbounds.Where(o => !templateTags.Contains(o.tag)).ToList();
            fragment.Balancers = _coreConfig.routing.balancers;
            fragment.Observatory = _coreConfig.observatory;
            fragment.BurstObservatory = _coreConfig.burstObservatory;
            return fragment;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return fragment;
        }
    }

    /// <summary>
    /// Возвращает Remarks профиля, если правило указывает на профиль типа Custom.
    /// Часть 1 такие цели не поддерживает — их обслуживают цепочечные ядра из Части 2.
    /// </summary>
    private string? ResolveUnsupportedCustomTarget(string? outboundTag)
    {
        if (outboundTag.IsNullOrEmpty() || Global.OutboundTags.Contains(outboundTag))
        {
            return null;
        }
        var node = context.AllProxiesMap.GetValueOrDefault($"remark:{outboundTag}");
        return node?.ConfigType == EConfigType.Custom ? outboundTag : null;
    }
}
