using System;
using System.Net;
using System.Net.Http;
using CentrePC.Core;
using Xunit;

namespace CentrePC.Tests;

public class RateLimitTests
{
    [Fact]
    public void Demandes_de_ralentir_reconnues()
    {
        Assert.True(OnlineClassifier.IsBusy(new HttpResponseMessage((HttpStatusCode)429)));
        Assert.True(OnlineClassifier.IsBusy(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var quota = new HttpResponseMessage(HttpStatusCode.Forbidden);
        quota.Headers.Add("x-ratelimit-remaining", "0");
        Assert.True(OnlineClassifier.IsBusy(quota));
        // Un vrai refus ou une page absente ne sont pas des demandes de patienter
        Assert.False(OnlineClassifier.IsBusy(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.False(OnlineClassifier.IsBusy(new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.False(OnlineClassifier.IsBusy(new HttpResponseMessage(HttpStatusCode.OK)));
    }

    [Fact]
    public void Duree_demandee_par_le_service()
    {
        var r = new HttpResponseMessage((HttpStatusCode)429);
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(20), OnlineClassifier.WaitAsked(r));
        // Sans indication : une minute
        Assert.Equal(TimeSpan.FromMinutes(1), OnlineClassifier.WaitAsked(new HttpResponseMessage((HttpStatusCode)429)));
    }

    [Fact]
    public void Requetes_espacees_par_service()
    {
        var t0 = DateTime.UtcNow.AddHours(5);   // loin dans le futur : indépendant des autres tests
        var gap = OnlineClassifier.Spacing["GitHub"];
        Assert.Equal(TimeSpan.Zero, OnlineClassifier.ReserveTurn("Test-espacement", t0));
        OnlineClassifier.Spacing["Test-espacement-2"] = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.Zero, OnlineClassifier.ReserveTurn("Test-espacement-2", t0));
        Assert.Equal(TimeSpan.FromSeconds(2), OnlineClassifier.ReserveTurn("Test-espacement-2", t0));
        Assert.Equal(TimeSpan.FromSeconds(4), OnlineClassifier.ReserveTurn("Test-espacement-2", t0));
        Assert.True(gap >= TimeSpan.FromSeconds(6));   // GitHub : 10 recherches par minute au plus sans compte
    }

    [Fact]
    public void Service_en_pause_saute_puis_repris()
    {
        OnlineClassifier.Pause("Test-pause", TimeSpan.FromMinutes(10));
        Assert.Null(OnlineClassifier.ReserveTurn("Test-pause", DateTime.UtcNow));               // pause longue : source sautée
        Assert.NotNull(OnlineClassifier.ReserveTurn("Test-pause", DateTime.UtcNow.AddMinutes(11))); // après la pause : reprise
        OnlineClassifier.Pause("Test-pause-courte", TimeSpan.FromSeconds(5));
        var wait = OnlineClassifier.ReserveTurn("Test-pause-courte", DateTime.UtcNow);
        Assert.NotNull(wait);                                                                    // pause courte : on attend son tour
        Assert.True(wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(5));
    }
}
