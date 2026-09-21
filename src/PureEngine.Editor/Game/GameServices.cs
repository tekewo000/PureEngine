using Microsoft.Extensions.DependencyInjection;
using PureEngine.Editor.Samples;

namespace PureEngine.Editor;

/// <summary>
/// Editor側のゲーム用サービス登録（サンプル）。編集・Playとも同じ登録処理を
/// <see cref="Runtime.GameSession"/>.Create／<see cref="Runtime.PlaySession"/>.Prepareへ渡して使う。
/// Component 自体の DI 登録は不要。既存の ComponentRegistry への型登録は別の役割として残す。
/// A1でプロジェクト側の登録口が用意できたら、そちらへ置き換える接続点。
/// </summary>
public static class GameServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
    }
}
