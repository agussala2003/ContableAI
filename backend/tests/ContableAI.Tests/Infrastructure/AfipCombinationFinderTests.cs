using ContableAI.Domain.Entities;
using ContableAI.Infrastructure.Features.Afip;
using FluentAssertions;

namespace ContableAI.Tests.Infrastructure;

/// <summary>
/// Tests del buscador de combinaciones de VEPs (Fase 4 — reunión 15-7-2026): un único débito
/// bancario a AFIP puede corresponder al pago agrupado de 2+ VEPs de ARCA. El sistema debe
/// SUGERIR las combinaciones cuya sumatoria coincida exactamente, nunca aplicarlas solo.
/// </summary>
public class AfipCombinationFinderTests
{
    private static AfipVoucher V(decimal amount, string tax = "IVA A Pagar", int day = 24) => new()
    {
        CompanyId = Guid.NewGuid(),
        Date      = new DateOnly(2025, 2, day),
        Amount    = amount,
        TaxName   = tax,
    };

    [Fact]
    public void Find_TranscriptCase_IvaPlusIibbSumExactly()
    {
        // Caso literal de la reunión: 422.350,68 (IVA) + 13.900,42 (IIBB) = 436.251,10
        var iva  = V(422_350.68m, "IVA A Pagar");
        var iibb = V(13_900.42m, "Pago IIBB");
        var otro = V(170_662.65m, "Plan de Facilidades");

        var combos = AfipCombinationService.FindExactCombinations([iva, iibb, otro], 436_251.10m);

        combos.Should().ContainSingle();
        combos[0].Should().BeEquivalentTo(new[] { iva, iibb });
    }

    [Fact]
    public void Find_ThreeVoucherCombination()
    {
        var a = V(100.10m);
        var b = V(200.20m);
        var c = V(300.30m);
        var noise = V(999.99m);

        var combos = AfipCombinationService.FindExactCombinations([a, b, c, noise], 600.60m);

        combos.Should().ContainSingle();
        combos[0].Should().BeEquivalentTo(new[] { a, b, c });
    }

    [Fact]
    public void Find_NoMatch_ReturnsEmpty()
    {
        var combos = AfipCombinationService.FindExactCombinations(
            [V(100m), V(200m), V(50m)], 999.99m);

        combos.Should().BeEmpty();
    }

    [Fact]
    public void Find_NeverUsesToleranceOrPartialSums()
    {
        // 100 + 200 = 300; target 300.01 no debe matchear (igualdad decimal estricta)
        var combos = AfipCombinationService.FindExactCombinations(
            [V(100m), V(200m)], 300.01m);

        combos.Should().BeEmpty();
    }

    [Fact]
    public void Find_ExcludesSingleVoucherEqualToTarget()
    {
        // Un VEP que ya iguala el importe es dominio del cruce 1:1 automático, no de combos
        var exact = V(500m);
        var a = V(300m);
        var b = V(200m);

        var combos = AfipCombinationService.FindExactCombinations([exact, a, b], 500m);

        combos.Should().ContainSingle("solo la combinación de dos, nunca el voucher individual");
        combos[0].Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact]
    public void Find_MultipleAlternatives_AllReturned()
    {
        // Dos formas distintas de sumar 500: {300, 200} y {450, 50}
        var combos = AfipCombinationService.FindExactCombinations(
            [V(300m), V(200m), V(450m), V(50m)], 500m);

        combos.Should().HaveCount(2);
        combos.Should().OnlyContain(c => c.Sum(v => v.Amount) == 500m);
    }

    [Fact]
    public void Find_RespectsMaxResults()
    {
        // Muchos pares (100+400): con maxResults=3 corta ahí
        var vouchers = Enumerable.Range(0, 6).Select(_ => V(100m))
            .Concat(Enumerable.Range(0, 6).Select(_ => V(400m)))
            .ToList();

        var combos = AfipCombinationService.FindExactCombinations(vouchers, 500m, maxResults: 3);

        combos.Should().HaveCount(3);
    }

    [Fact]
    public void Find_RespectsMaxSize()
    {
        // 5 × 100 = 500 pero con maxSize=3 no puede usar 5 vouchers
        var vouchers = Enumerable.Range(0, 5).Select(_ => V(100m)).ToList();

        var combos = AfipCombinationService.FindExactCombinations(vouchers, 500m, maxSize: 3);

        combos.Should().BeEmpty();
    }

    [Fact]
    public void Find_EveryComboHasAtLeastTwoVouchers()
    {
        var combos = AfipCombinationService.FindExactCombinations(
            [V(250m), V(250m), V(100m), V(400m)], 500m);

        combos.Should().NotBeEmpty();
        combos.Should().OnlyContain(c => c.Count >= 2, "las sugerencias 1:1 no son combos");
    }

    [Fact]
    public void Find_LargeCandidateList_TerminatesQuickly()
    {
        // Poda: 60 vouchers sin solución no debe explotar combinatoriamente
        var vouchers = Enumerable.Range(1, 60).Select(i => V(i * 7.77m)).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var combos = AfipCombinationService.FindExactCombinations(vouchers, 0.01m);
        sw.Stop();

        combos.Should().BeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
}
