namespace VznBooster.Core.Networking;

/// <summary>
/// Região de um jogo e o destino AUXILIAR usado como referência de proximidade (endpoint regional público da AWS, documentado pela AWS).
/// Não é o servidor do jogo. Nenhum endereço de servidor de jogo é usado ou inventado.
/// </summary>
public sealed record GameRegion(string Id, string Name, string Host, string Reference);

public sealed record GameProfile(string Id, string Name, string Disclaimer, IReadOnlyList<GameRegion> Regions);

public static class GameCatalog
{
    public const string AwsEndpointsDocumentation = "https://docs.aws.amazon.com/general/latest/gr/ec2-service.html";
    public const int Port = 443;

    private static GameRegion Aws(string id, string name, string awsRegion, string reference) =>
        new(id, name, $"ec2.{awsRegion}.amazonaws.com", $"{reference} ({awsRegion})");

    public static IReadOnlyList<GameProfile> All { get; } = new[]
    {
        new GameProfile("valorant", "Valorant",
            "Não há destino oficial e verificado de teste de ping do VALORANT disponível ao VZN Booster. O servidor da partida NÃO é medido.",
            new[]
            {
                Aws("br", "Brasil (BR)", "sa-east-1", "Datacenter AWS em São Paulo"),
                Aws("latam", "América Latina (LATAM)", "sa-east-1", "Datacenter AWS em São Paulo (aproximação: o servidor pode estar em outra cidade)"),
                Aws("na", "América do Norte (NA)", "us-east-1", "Datacenter AWS na Virgínia"),
                Aws("eu", "Europa (EU)", "eu-central-1", "Datacenter AWS em Frankfurt"),
                Aws("ap", "Ásia-Pacífico (AP)", "ap-southeast-1", "Datacenter AWS em Singapura"),
                Aws("kr", "Coreia (KR)", "ap-northeast-2", "Datacenter AWS em Seul"),
            }),
        new GameProfile("rocketleague", "Rocket League",
            "Não há destino oficial e verificado de teste de ping do Rocket League disponível ao VZN Booster. O servidor da partida NÃO é medido.",
            new[]
            {
                Aws("sam", "América do Sul (Brasil)", "sa-east-1", "Datacenter AWS em São Paulo"),
                Aws("use", "EUA Leste", "us-east-1", "Datacenter AWS na Virgínia"),
                Aws("usw", "EUA Oeste", "us-west-1", "Datacenter AWS na Califórnia"),
                Aws("eu", "Europa", "eu-central-1", "Datacenter AWS em Frankfurt"),
                Aws("oce", "Oceania", "ap-southeast-2", "Datacenter AWS em Sydney"),
            }),
        new GameProfile("fortnite", "Fortnite",
            "Não há destino oficial e verificado de teste de ping do Fortnite disponível ao VZN Booster. O servidor da partida NÃO é medido.",
            new[]
            {
                Aws("br", "Brasil", "sa-east-1", "Datacenter AWS em São Paulo"),
                Aws("nae", "América do Norte — Leste", "us-east-1", "Datacenter AWS na Virgínia"),
                Aws("naw", "América do Norte — Oeste", "us-west-1", "Datacenter AWS na Califórnia"),
                Aws("eu", "Europa", "eu-central-1", "Datacenter AWS em Frankfurt"),
                Aws("oce", "Oceania", "ap-southeast-2", "Datacenter AWS em Sydney"),
                Aws("asia", "Ásia", "ap-northeast-1", "Datacenter AWS em Tóquio (aproximação)"),
            }),
    };

    public static GameProfile? Find(string id) => All.FirstOrDefault(g => g.Id == id);
}
