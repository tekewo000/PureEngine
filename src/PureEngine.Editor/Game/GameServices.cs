using Microsoft.Extensions.DependencyInjection;
using PureEngine.Editor.Samples;

namespace PureEngine.Editor;

/// <summary>
/// Game 側のサービス登録口（一箇所）。編集・Play・単体実行で同じ登録処理を使う。
/// Component 自体の DI 登録は不要。既存の ComponentRegistry への型登録は別の役割として残す。
/// </summary>
public static class GameServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
    }
}
