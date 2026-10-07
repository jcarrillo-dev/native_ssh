using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    public sealed partial class MotoresBusquedaTests
    {
        // Un subdirectorio y un archivo ilegibles dentro del árbol: los cuatro motores los omiten sin mensaje y salen con
        // el código 2, del que ssh_search avisa sin detalle («pueden faltar coincidencias»). rg y grep de GNU lo hacen así
        // por sí mismos; el recorrido con find calla el «Permission denied» de find y del grep de su -exec y, si solo calló
        // eso, sale con 2 (antes, con 0: era el único motor que no avisaba; A-undecies, AU3).
        [Theory]
        [MemberData(nameof(Motores))]
        public void SubdirectorioIlegibleDentroDelArbol_TodosLosMotoresSalenConDosSinMensaje(string motor)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            Escribir("i/a.txt", "valor visible");
            Escribir("i/cerrado/x.txt", "valor cerrado");
            string cerrado = Path.Combine(_datos, "i", "cerrado");
            File.SetUnixFileMode(cerrado, UnixFileMode.None);

            try
            {
                // Act
                (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor", "i", null, 50));

                // Assert
                _ = (codigo, salida, errores).Should().Be((2, $"__NS_MOTOR:{anunciado}\ni/a.txt:1:valor visible\n", string.Empty));
            }
            finally
            {
                File.SetUnixFileMode(cerrado, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // Solo un archivo ilegible dentro del árbol (ningún directorio): también aquí los cuatro motores salen con 2 sin
        // mensaje. En el recorrido con find, el «Permission denied» del archivo lo da el grep del -exec, que antes iba con -s
        // y lo callaba: ese motor salía con 0 y ssh_search no avisaba de que podían faltar coincidencias (O1 del A-undecies).
        [Theory]
        [MemberData(nameof(Motores))]
        public void SoloUnArchivoIlegibleDentroDelArbol_TodosLosMotoresSalenConDosSinMensaje(string motor)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            Escribir("j/a.txt", "valor visible");
            Escribir("j/cerrado.txt", "valor cerrado");
            string cerrado = Path.Combine(_datos, "j", "cerrado.txt");
            File.SetUnixFileMode(cerrado, UnixFileMode.None);

            try
            {
                // Act
                (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor", "j", null, 50));

                // Assert
                _ = (codigo, salida, errores).Should().Be((2, $"__NS_MOTOR:{anunciado}\nj/a.txt:1:valor visible\n", string.Empty));
            }
            finally
            {
                File.SetUnixFileMode(cerrado, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
