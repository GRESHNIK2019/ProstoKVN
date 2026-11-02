using System;

namespace ProstoKVN;

public static class RouteAssessment
{
    public static string Describe(string domain, RouteComparison result, RouteRule? rule)
    {
        string direct = Format(result.Direct);
        string secured = Format(result.Vpn);
        string route = rule == null ? "AUTO (по правилам)" :
            rule.Route == "VEGA" ? "СЕРВЕР" : rule.Route == "REJECT" ? "BLOCK" : "DIRECT";
        if (rule != null && !string.IsNullOrWhiteSpace(rule.PreferredNode))
            route += " / " + rule.PreferredNode;
        if (rule?.Provisional == true)
            route += " (пробный AUTO, работа сайта в браузере не подтверждена)";
        string outcome;
        if (result.Direct.State == ProbeState.Restricted && result.Vpn.State == ProbeState.Restricted)
            outcome = "Оба автоматических запроса получили отказ. Сайт всё равно может работать через выбранный сервер в обычном браузере. " +
                (rule?.Route == "VEGA" ? "Маршрут уже назначен." : "Блокировка не подтверждена; автоматическое правило не создаётся.");
        else if (result.Direct.Usable && result.Vpn.Usable)
            outcome = "Оба маршрута отвечают на HTTP-проверку; работу сайта нужно проверить в браузере.";
        else if (result.Direct.Usable)
            outcome = "Прямое соединение прошло проверку. Для этого домена предпочтителен DIRECT.";
        else if (result.Vpn.Usable)
            outcome = "Проверку прошёл маршрут через сервер. Прямое соединение не прошло.";
        else
            outcome = "ДОСТУПНОСТЬ НЕ ПОДТВЕРЖДЕНА. Автоматическая проверка не гарантирует работу сайта.";
        return $"Проверка {domain}\nНапрямую: {direct}\nЧерез сервер: {secured}\nТекущее правило: {route}\nИтог: {outcome}";
    }

    public static string Format(RouteProbe p)
    {
        string status = p.State switch
        {
            ProbeState.Restricted => "HTTP-ОТКАЗ (браузер не проверен)",
            ProbeState.Failed => "ОШИБКА СОЕДИНЕНИЯ",
            ProbeState.Unknown => "НЕ ПОДТВЕРЖДЁН",
            _ => "HTTP-ответ получен"
        };
        return $"{status}; {(p.HttpCode.HasValue ? "HTTP " + p.HttpCode.Value + "; " : "")}{p.Reason}";
    }
}
