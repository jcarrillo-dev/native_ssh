using FluentAssertions;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ;
using JCarrillo.AOT.Core.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class ValueLinqDelayExtensionesTests
    {
        private readonly struct PredicadoEsPar : IWhereDelegado<int, int>
        {
            public bool Ejecutar(int item, int divisor) => item % divisor == 0;
        }

        [Fact]
        public void Take_MenorQueTotal_LimitaElementos()
        {
            int[] origen = [1, 2, 3, 4, 5];
            List<int> resultado = [];
            foreach (int item in origen.ToValueQuery().Delay().Take(3))
                resultado.Add(item);

            _ = resultado.Should().Equal(1, 2, 3);
        }

        [Fact]
        public void Take_MayorQueTotal_DevuelveTodos()
        {
            int[] origen = [1, 2, 3];
            List<int> resultado = [];
            foreach (int item in origen.ToValueQuery().Delay().Take(10))
                resultado.Add(item);

            _ = resultado.Should().Equal(1, 2, 3);
        }

        [Fact]
        public void Take_CeroONegativo_DevuelveVacio()
        {
            int[] origen = [1, 2, 3];
            List<int> resultado = [];
            foreach (int item in origen.ToValueQuery().Delay().Take(0))
                resultado.Add(item);

            _ = resultado.Should().BeEmpty();
        }

        [Fact]
        public void Any_ColeccionConElementos_DevuelveTrue()
        {
            int[] origen = [42];
            bool has = origen.ToValueQuery().Delay().Any();
            _ = has.Should().BeTrue();
        }

        [Fact]
        public void Any_ColeccionVacia_DevuelveFalse()
        {
            int[] origen = [];
            bool has = origen.ToValueQuery().Delay().Any();
            _ = has.Should().BeFalse();
        }

        [Fact]
        public void Any_ConPredicado_ElementoCoincide_DevuelveTrue()
        {
            int[] origen = [1, 3, 4, 7];
            PredicadoEsPar predicado = default;
            bool has = origen.ToValueQuery().Delay().Any(2, ref predicado);
            _ = has.Should().BeTrue();
        }

        [Fact]
        public void Any_ConPredicado_NingunoCoincide_DevuelveFalse()
        {
            int[] origen = [1, 3, 5, 7];
            PredicadoEsPar predicado = default;
            bool has = origen.ToValueQuery().Delay().Any(2, ref predicado);
            _ = has.Should().BeFalse();
        }

        [Fact]
        public void Dispose_EsIdempotente()
        {
            int[] origen = [1, 2, 3];
            ValueTakeDelay<int, ValueLINQSessionEnumerator<int>> enumerator = origen.ToValueQuery().Delay().Take(2);
            enumerator.Dispose();
            enumerator.Dispose();
            enumerator.Dispose();

            ValueTakeDelay<int, ValueLINQSessionEnumerator<int>> enumeratorConsumido = origen.ToValueQuery().Delay().Take(1);
            _ = enumeratorConsumido.MoveNext();
            _ = enumeratorConsumido.MoveNext();
            enumeratorConsumido.Dispose();
            enumeratorConsumido.Dispose();
        }

        [Fact]
        public void Take_Composabilidad_PermiteEncadenarConAny()
        {
            int[] origen = [1, 2, 3];
            bool hasElementos = origen.ToValueQuery().Delay().Take(2).Any();
            _ = hasElementos.Should().BeTrue();
        }
    }
}
