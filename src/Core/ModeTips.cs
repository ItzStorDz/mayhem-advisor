using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>Un consejo general: "mode" = cómo funciona ARAM: Caos; "app" = algo de la app que se usa poco.</summary>
public sealed record GeneralTip(string Kind, string Text);

/// <summary>
/// Consejos generales para la pantalla de inicio («Consejo del día» y «Ver todos»). Las reglas del modo salen de la wiki de
/// League of Legends (wiki.leagueoflegends.com/en-us/ARAM:_Mayhem, consultada el 2026-11-05, parche 16.22) y de nuestros
/// datos; escritos con nuestras palabras. Si Riot cambia una regla, hay que cambiarla aquí.
/// </summary>
public static class ModeTips
{
    public static IReadOnlyList<GeneralTip> All(string? spellsFact = null)
    {
        var list = new List<GeneralTip>
        {
            Mode("Los aumentos salen al empezar (nivel 3) y al llegar a los niveles 7, 11 y 15. Cada una de las tres cartas se puede cambiar una vez, por otra de la misma rareza: hasta 6 opciones en cada elección.",
                 "Augments come at the start (level 3) and when you reach levels 7, 11 and 15. Each of the three cards can be rerolled once, for another of the same rarity: up to 6 options per pick."),
            Mode("Las cartas solo salen cuando puedes comprar (muerto o en la base al reaparecer). Si las escondes para mirar el mapa, la tienda no se abre hasta que elijas.",
                 "Cards only show up when you can shop (dead, or at base after respawning). If you hide them to look at the map, the shop stays closed until you pick."),
            Mode("En Caos el crítico por encima del 100 % no se pierde del todo: cada 1 % de más da 0,45 de daño de ataque o 0,75 de poder de habilidad. Aun así, otro objeto de crítico rinde mucho menos que uno de daño.",
                 "In Mayhem, crit above 100% isn't fully wasted: each extra 1% gives 0.45 attack damage or 0.75 ability power. Still, another crit item is worth far less than a damage item."),
            Mode("La velocidad de ataque llega hasta 5,0 (no 2,5 como en la Grieta): las builds de ataques rápidos y efectos al impactar tienen más recorrido.",
                 "Attack speed goes up to 5.0 (not 2.5 like on the Rift): fast-attack and on-hit builds have more room to grow."),
            Mode("Rompecombos: si te inmovilizan 5 de los últimos 7 segundos, te limpias y tienes 3 segundos sin control. Contra equipos de mucho control, aguanta: llega solo.",
                 "Combo Breaker: if you're immobilized 5 of the last 7 seconds, you're cleansed and get 3 seconds of CC immunity. Against heavy-CC teams, hold on: it comes on its own."),
            Mode("Las reliquias de vida curan el 22 % de la vida y del maná que te faltan. Cogerlas antes de una pelea vale más que ir con media vida.",
                 "Health relics restore 22% of your missing health and mana. Taking one before a fight beats going in at half health."),
            Mode("El nexo tiene 3.000 de vida (en ARAM normal, 5.500): cuando cae un inhibidor, una pelea ganada puede acabar la partida.",
                 "The Nexus has 3,000 health (5,500 in normal ARAM): once an inhibitor falls, one won fight can end the game."),
            Mode("Desde el nivel 9 puedes gastar el oro que te sobra en «Estadística adicional» (750 de oro): es un yunque de estadísticas y además te dan un Juguito de Gorrito. La app puntúa sus fragmentos cuando salen.",
                 "From level 9 you can spend spare gold on “Stat Bonus” (750 gold): it's a Stat Anvil and you also get a free Cappa Juice. The app scores its shards when they show up."),
            Mode("No hay runas ni Extenuación: algunas piedras angulares llegan como aumentos, así que mira bien la descripción de cada carta.",
                 "There are no runes and no Exhaust: some keystones come as augments, so read each card's description."),
            Mode("Casi nadie empieza por las botas en Caos (en aramkit, el 0-18 % según el campeón) y el 26-38 % no las compra nunca: primero tu objeto, luego las botas.",
                 "Almost nobody opens with boots in Mayhem (0-18% depending on the champion, per aramkit) and 26-38% never buy them: your item first, then boots."),
            App("Al morir, el panel se abre solo en «Compra»: tienes pocos segundos de tienda, y lo primero que ves es qué comprar y qué pieza te llega ya.",
                "When you die the panel opens on “Buy” by itself: you have a few seconds of shop, and the first thing you see is what to buy and which part you can afford."),
            App("En «TIPO DE BUILD» del panel puedes fijar una build (on-hit, AP, tanque…): todas las compras la siguen el resto de la partida. Púlsala otra vez para que vuelva a elegir la app.",
                "In the panel's “BUILD TYPE” you can lock a build (on-hit, AP, tank…): every purchase follows it for the rest of the game. Tap it again to let the app choose."),
            App("«Me quedo con…» en el panel: la app no te dirá que cambies ese objeto que ya estás haciendo.",
                "“Keep building…” in the panel: the app won't tell you to switch away from an item you're already building."),
            App("La pestaña «Adaptar» del panel te dice cuánto daño mágico y físico hace el equipo rival y qué comprar contra él (resistencias, heridas graves, antiescudos…).",
                "The panel's “Adapt” tab tells you how much magic and physical damage the enemy team deals and what to buy against it (resists, grievous wounds, anti-shield…)."),
            App("¿El panel te tapa algo? Arrástralo por la barra de arriba, cambia su tamaño desde la esquina de abajo a la izquierda o pulsa ⊟ para dejar solo «Compra ahora».",
                "Is the panel in the way? Drag it by the top bar, resize it from the bottom-left corner, or press ⊟ to keep just “Buy now”."),
            App("En la selección de campeón, el panel ordena tu campeón y los del banco por cómo ganan en Caos: «Coger» te lo cambia con un clic.",
                "In champion select, the panel ranks your champion and the bench by their Mayhem win rate: “Take” swaps it in one click."),
            App("En la selección de campeón la app deja su build en la tienda del juego (pestaña de builds recomendadas), con qué cambiar contra mucho daño mágico o físico.",
                "In champion select the app puts its build in the in-game shop (recommended builds tab), with what to change against heavy magic or physical damage."),
            App("Las cartas se leen de la pantalla: con el juego en pantalla completa no se ve nada. Usa «sin bordes» (el panel de la selección de campeón lo cambia con un botón).",
                "Cards are read from the screen: in fullscreen nothing can be seen. Use borderless (the champion select panel switches it with one button)."),
            App("En «Partidas» tienes el resumen de cada partida: tu puesto entre los 10, la nota de tus aumentos y una imagen para pegar en Discord o WhatsApp.",
                "“Games” has a summary of each game: your place among the 10, your augment scores and an image to paste in Discord or WhatsApp."),
            App("¿Algo raro o una idea? Cuéntalo en «Sugerencias»: lo lee el creador de la app y sirve para decidir qué arreglar primero.",
                "Something odd or an idea? Tell us in “Suggestions”: the app's creator reads it and uses it to decide what to fix first."),
        };
        if (spellsFact is not null) list.Insert(9, Mode(spellsFact, spellsFact));
        return list;
    }

    static GeneralTip Mode(string es, string en) => new("mode", L(es, en));
    static GeneralTip App(string es, string en) => new("app", L(es, en));
}
