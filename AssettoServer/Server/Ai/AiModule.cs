using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Configuration.Extra;
using AssettoServer.Server.OpenSlotFilters;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace AssettoServer.Server.Ai;

public class AiModule : Module
{
    private readonly ACServerConfiguration _configuration;

    public AiModule(ACServerConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<AiState>().AsSelf();

        if (_configuration.Extra.EnableAi)
        {
            builder.RegisterType<AiBehavior>().AsSelf().As<IHostedService>().SingleInstance();
            builder.RegisterType<AiUpdater>().AsSelf().SingleInstance().AutoActivate();
            builder.RegisterType<AiSlotFilter>().As<IOpenSlotFilter>();
            // ASFALTO BetterTraffic: always registered (plugins may use it), inactive unless AiParams.BetterTraffic.Enabled
            builder.RegisterType<BetterTrafficFlashDetector>().AsSelf().SingleInstance().AutoActivate();
            builder.RegisterType<BetterTrafficCrashes>().AsSelf().SingleInstance().AutoActivate();

            var bt = _configuration.Extra.AiParams.BetterTraffic;
            if (bt.Enabled && bt.DensityPreset != TrafficDensityPreset.None)
            {
                // density preset: fills the hourly density, which AssettoServer blends between hours
                _configuration.Extra.AiParams.HourlyTrafficDensity = TrafficDensityPresets.Hourly(bt.DensityPreset);
            }
            
            if (_configuration.Extra.AiParams.HourlyTrafficDensity != null)
            {
                builder.RegisterType<DynamicTrafficDensity>().As<IHostedService>().SingleInstance();
            }

            builder.RegisterType<AiSplineWriter>().AsSelf();
            builder.RegisterType<FastLaneParser>().AsSelf();
            builder.RegisterType<AiSplineLocator>().AsSelf();
            builder.Register((AiSplineLocator locator) => locator.Locate()).AsSelf().SingleInstance();
        }
    }
}
