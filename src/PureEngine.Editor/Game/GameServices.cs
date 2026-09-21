using Microsoft.Extensions.DependencyInjection;
using PureEngine.Editor.Samples;

namespace PureEngine.Editor;

/// <summary>
/// Editor側のゲーム用サービス登録（サンプル）。編集・Playとも同じ登録処理を
/// <see cref="Runtime.GameSession"/>.Create／<see cref="Runtime.PlaySession"/>.Prepareへ渡して使う。
/// Component 自体の DI 登録は不要。既存の ComponentRegistry への型登録は別の役割として残す。
/// ForProject／ForUserCodeで組み込み登録と当該プロジェクトの登録を組み合わせる。
/// </summary>
public static class GameServices
{
    public static Action<IServiceCollection> ForProject(ProjectComponents components) =>
        ForUserCode(components.ActiveUserCode);

    public static Action<IServiceCollection> ForUserCode(UserCodeCompileResult? userCode) => services =>
    {
        Configure(services);
        ProjectGameServices.Apply(userCode, services);
    };

    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
    }
}
