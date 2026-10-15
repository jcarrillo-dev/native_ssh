// Tarea de MSBuild (compilada en un ensamblado por el target NativeSshCompilarTareaLinq de Directory.Build.props): falla si
// el ensamblado compilado hace referencia a System.Linq o a un espacio de nombres bajo él. Mira el resultado y no el fuente,
// así que no importa cómo se escribió ni si lo generó el compilador ([.. hashSet] se compila a Enumerable.ToArray; una
// lambda convertida en árbol de expresión, a System.Linq.Expressions). No es parte de ningún proyecto: no está bajo la carpeta de ninguno.
//
// Qué se comprueba:
// - cada TypeRef (también los anidados, por el espacio de nombres del tipo exterior): cualquier uso de un tipo de otro
//   ensamblado (llamada, campo, firma, local, base, restricción, atributo, TypeSpec y MethodSpec) pasa por uno;
// - los tipos propios declarados en System.Linq o bajo él;
// - los typeof de los argumentos de los atributos, que se guardan como un nombre de tipo serializado (sin TypeRef);
// - los nombres de tipo en string de UnsafeAccessorTypeAttribute y DynamicDependencyAttribute: el runtime, el trimmer y AOT
//   los resuelven como una referencia al tipo (sin TypeRef);
// - los ExportedType (reenvíos de tipo, TypeForwardedTo) a System.Linq o bajo él.
// Para el mensaje se busca dónde se usa (método con su fichero y línea si hay PDB portable, firma, campo, propiedad, evento,
// base, restricción o atributo); si no se encuentra, el error nombra el tipo igualmente. La reflexión (un nombre de tipo en un
// string fuera de esos dos atributos) queda fuera.
//
// Se compila contra el netstandard.dll de MSBuild: las referencias son las builds para .NET Framework de
// System.Reflection.Metadata y System.Collections.Immutable que trae el SDK (Sdks/Microsoft.NET.Sdk/tools/net472); las del
// paquete de referencia de net10.0 no se pueden mezclar con netstandard. Al ejecutarse, MSBuild usa las del runtime.
// Escrita con las reglas de MSBuild (sin las del proyecto, que no se aplican aquí).

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class NativeSshComprobarSystemLinqEnIL : Task
{
    private const int MaxErrores = 40;

    [Required]
    public string Ensamblado { get; set; } = "";

    // PDB portable separado (opcional): solo para dar el fichero y la línea.
    public string Simbolos { get; set; } = "";

    public ITaskItem[] Analizadores { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        bool hasAnalizador = false;
        foreach (ITaskItem analizador in Analizadores)
            if (string.Equals(analizador.GetMetadata("Filename"), "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers", StringComparison.OrdinalIgnoreCase))
                hasAnalizador = true;
        if (!hasAnalizador)
            Log.LogError(null, "NSLINQ", null, null, 0, 0, 0, 0, "Falta el analizador Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers, que avisa de System.Linq en el editor (RS0030).");

        if (!File.Exists(Ensamblado))
        {
            Log.LogError(null, "NSLINQ", null, null, 0, 0, 0, 0, "No existe el ensamblado compilado que se debe comprobar: {0}", Ensamblado);
            return false;
        }

        // Un ensamblado que no se puede leer es un error del build (nunca un éxito sin comprobar).
        try
        {
            using (FileStream fichero = new FileStream(Ensamblado, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (PEReader pe = new PEReader(fichero))
            {
                Comprobador comprobador = new Comprobador(pe, pe.GetMetadataReader());
                List<Hallazgo> hallazgos = comprobador.Comprobar();
                if (hallazgos.Count > 0)
                    Informar(hallazgos);
            }
        }
        catch (Exception excepcion) when (excepcion is BadImageFormatException || excepcion is InvalidOperationException || excepcion is IOException || excepcion is UnauthorizedAccessException)
        {
            Log.LogError(null, "NSLINQ", null, Ensamblado, 0, 0, 0, 0, "No se pudo comprobar System.Linq en el ensamblado compilado: {0}", excepcion.Message);
        }

        return !Log.HasLoggedErrors;
    }

    private void Informar(List<Hallazgo> hallazgos)
    {
        MetadataReader? pdb = null;
        MetadataReaderProvider? proveedor = null;
        // El PDB solo da el fichero y la línea: si no se puede leer (no es portable, falta, está a medias), sin ellos.
        FileStream? simbolos = null;
        try
        {
            if (Simbolos.Length > 0 && File.Exists(Simbolos))
            {
                simbolos = new FileStream(Simbolos, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                proveedor = MetadataReaderProvider.FromPortablePdbStream(simbolos);
                simbolos = null;
                pdb = proveedor.GetMetadataReader();
            }
        }
        catch (Exception excepcion) when (excepcion is BadImageFormatException || excepcion is InvalidOperationException || excepcion is IOException || excepcion is UnauthorizedAccessException)
        {
            pdb = null;
        }
        finally
        {
            simbolos?.Dispose();
        }

        try
        {
            HashSet<string> vistos = new HashSet<string>(StringComparer.Ordinal);
            int escritos = 0;
            foreach (Hallazgo hallazgo in hallazgos)
            {
                string fuente = "";
                int linea = 0;
                if (pdb != null && !hallazgo.Metodo.IsNil && hallazgo.Desplazamiento >= 0)
                    BuscarLinea(pdb, hallazgo.Metodo, hallazgo.Desplazamiento, out fuente, out linea);
                string mensaje = hallazgo.Donde + " usa " + hallazgo.Que;
                if (!vistos.Add(fuente + ":" + linea + ":" + mensaje))
                    continue;
                if (++escritos > MaxErrores)
                    continue;
                Log.LogError(null, "NSLINQ", null, fuente.Length > 0 ? fuente : Ensamblado, linea, 0, 0, 0,
                    "System.Linq solo se puede usar en las pruebas (native_ssh.Test), y el ensamblado compilado lo referencia: {0}.", mensaje);
            }

            if (escritos > MaxErrores)
                Log.LogError(null, "NSLINQ", null, Ensamblado, 0, 0, 0, 0, "... y {0} referencias más a System.Linq.", escritos - MaxErrores);
        }
        finally
        {
            proveedor?.Dispose();
        }
    }

    // La línea del punto de secuencia visible más cercano por delante del desplazamiento IL.
    private static void BuscarLinea(MetadataReader pdb, MethodDefinitionHandle metodo, int desplazamiento, out string fuente, out int linea)
    {
        fuente = "";
        linea = 0;
        try
        {
            MethodDebugInformation info = pdb.GetMethodDebugInformation(metodo);
            if (info.SequencePointsBlob.IsNil)
                return;
            foreach (SequencePoint punto in info.GetSequencePoints())
            {
                if (punto.Offset > desplazamiento)
                    break;
                if (punto.IsHidden)
                    continue;
                fuente = pdb.GetString(pdb.GetDocument(punto.Document).Name);
                linea = punto.StartLine;
            }
        }
        catch (Exception excepcion) when (excepcion is BadImageFormatException || excepcion is ArgumentException || excepcion is InvalidOperationException)
        {
            // Un PDB que no es de este ensamblado: el error va sin línea.
            fuente = "";
            linea = 0;
        }
    }

    private struct Hallazgo
    {
        public string Donde;
        public string Que;
        public MethodDefinitionHandle Metodo;
        public int Desplazamiento;
    }

    private sealed class Comprobador
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _md;
        private readonly Dictionary<TypeReferenceHandle, string> _deLinq = new Dictionary<TypeReferenceHandle, string>();
        private readonly ProveedorFirmas _firmas;
        private readonly List<Hallazgo> _hallazgos = new List<Hallazgo>();

        public Comprobador(PEReader pe, MetadataReader md)
        {
            _pe = pe;
            _md = md;
            _firmas = new ProveedorFirmas(this);
        }

        public static bool IsEspacioLinq(string espacio) =>
            espacio == "System.Linq" || espacio.StartsWith("System.Linq.", StringComparison.Ordinal);

        public List<Hallazgo> Comprobar()
        {
            foreach (TypeReferenceHandle referencia in _md.TypeReferences)
            {
                string espacio = EspacioExterior(referencia);
                if (IsEspacioLinq(espacio))
                    _deLinq[referencia] = NombreReferencia(referencia);
            }

            foreach (TypeDefinitionHandle tipo in _md.TypeDefinitions)
            {
                TypeDefinition definicion = _md.GetTypeDefinition(tipo);
                if (IsEspacioLinq(_md.GetString(definicion.Namespace)))
                    Anotar(NombreTipo(tipo), "su propio espacio de nombres (" + _md.GetString(definicion.Namespace) + ")");
            }

            foreach (ExportedTypeHandle exportado in _md.ExportedTypes)
            {
                string espacio = EspacioExportado(exportado);
                if (IsEspacioLinq(espacio))
                    Anotar("el ensamblado (reenvío de tipo)", espacio + "." + _md.GetString(_md.GetExportedType(exportado).Name));
            }

            int antesDeAtributos = _hallazgos.Count;
            RevisarAtributos();
            if (_deLinq.Count == 0)
                return _hallazgos;

            int antes = _hallazgos.Count;
            foreach (TypeDefinitionHandle tipo in _md.TypeDefinitions)
                RevisarTipo(tipo);

            // Sin ningún uso localizado, el error nombra los tipos referenciados.
            if (_hallazgos.Count == antes && antes == antesDeAtributos)
                foreach (KeyValuePair<TypeReferenceHandle, string> par in _deLinq)
                    Anotar("el ensamblado", par.Value);
            return _hallazgos;
        }

        private void Anotar(string donde, string que, MethodDefinitionHandle metodo = default, int desplazamiento = -1) =>
            _hallazgos.Add(new Hallazgo { Donde = donde, Que = que, Metodo = metodo, Desplazamiento = desplazamiento });

        private string EspacioExterior(TypeReferenceHandle referencia)
        {
            TypeReference tipo = _md.GetTypeReference(referencia);
            while (tipo.ResolutionScope.Kind == HandleKind.TypeReference)
                tipo = _md.GetTypeReference((TypeReferenceHandle)tipo.ResolutionScope);
            return _md.GetString(tipo.Namespace);
        }

        // Espacio de nombres de un ExportedType (el del exterior si está anidado).
        private string EspacioExportado(ExportedTypeHandle exportado)
        {
            ExportedType tipo = _md.GetExportedType(exportado);
            while (tipo.Implementation.Kind == HandleKind.ExportedType)
                tipo = _md.GetExportedType((ExportedTypeHandle)tipo.Implementation);
            return _md.GetString(tipo.Namespace);
        }

        private string NombreReferencia(TypeReferenceHandle referencia)
        {
            TypeReference tipo = _md.GetTypeReference(referencia);
            string nombre = _md.GetString(tipo.Name);
            if (tipo.ResolutionScope.Kind == HandleKind.TypeReference)
                return NombreReferencia((TypeReferenceHandle)tipo.ResolutionScope) + "+" + nombre;
            string espacio = _md.GetString(tipo.Namespace);
            return espacio.Length > 0 ? espacio + "." + nombre : nombre;
        }

        private string NombreTipo(TypeDefinitionHandle tipo)
        {
            TypeDefinition definicion = _md.GetTypeDefinition(tipo);
            string nombre = _md.GetString(definicion.Name);
            TypeDefinitionHandle contenedor = definicion.GetDeclaringType();
            if (!contenedor.IsNil)
                return NombreTipo(contenedor) + "+" + nombre;
            string espacio = _md.GetString(definicion.Namespace);
            return espacio.Length > 0 ? espacio + "." + nombre : nombre;
        }

        // El primer tipo de System.Linq al que llega la referencia (o null): un TypeRef, un TypeSpec (genérico instanciado,
        // array...), un miembro (por su tipo o por su firma) o un método genérico instanciado.
        public string? DeLinq(EntityHandle entidad)
        {
            switch (entidad.Kind)
            {
                case HandleKind.TypeReference:
                    return _deLinq.TryGetValue((TypeReferenceHandle)entidad, out string? nombre) ? nombre : null;
                case HandleKind.TypeSpecification:
                    return _md.GetTypeSpecification((TypeSpecificationHandle)entidad).DecodeSignature(_firmas, null);
                case HandleKind.MemberReference:
                {
                    MemberReference miembro = _md.GetMemberReference((MemberReferenceHandle)entidad);
                    string? delPadre = DeLinq(miembro.Parent);
                    if (delPadre != null)
                        return delPadre + "::" + _md.GetString(miembro.Name);
                    return miembro.GetKind() == MemberReferenceKind.Method
                        ? DeLinqFirma(miembro.DecodeMethodSignature(_firmas, null))
                        : miembro.DecodeFieldSignature(_firmas, null);
                }
                case HandleKind.MethodSpecification:
                {
                    MethodSpecification especificacion = _md.GetMethodSpecification((MethodSpecificationHandle)entidad);
                    string? delMetodo = DeLinq(especificacion.Method);
                    if (delMetodo != null)
                        return delMetodo;
                    foreach (string? argumento in especificacion.DecodeSignature(_firmas, null))
                        if (argumento != null)
                            return argumento;
                    return null;
                }
                case HandleKind.StandaloneSignature:
                {
                    StandaloneSignature firma = _md.GetStandaloneSignature((StandaloneSignatureHandle)entidad);
                    return firma.GetKind() == StandaloneSignatureKind.Method ? DeLinqFirma(firma.DecodeMethodSignature(_firmas, null)) : DeLinqLista(firma.DecodeLocalSignature(_firmas, null));
                }
                default:
                    return null;
            }
        }

        public static string? DeLinqFirma(MethodSignature<string?> firma) =>
            firma.ReturnType ?? DeLinqLista(firma.ParameterTypes);

        public static string? DeLinqLista(ImmutableArray<string?> tipos)
        {
            foreach (string? tipo in tipos)
                if (tipo != null)
                    return tipo;
            return null;
        }

        private void RevisarTipo(TypeDefinitionHandle manejador)
        {
            TypeDefinition tipo = _md.GetTypeDefinition(manejador);
            string nombreTipo = NombreTipo(manejador);
            string? encontrado;

            if (!tipo.BaseType.IsNil && (encontrado = DeLinq(tipo.BaseType)) != null)
                Anotar(nombreTipo + " (tipo base)", encontrado);
            foreach (InterfaceImplementationHandle interfaz in tipo.GetInterfaceImplementations())
                if ((encontrado = DeLinq(_md.GetInterfaceImplementation(interfaz).Interface)) != null)
                    Anotar(nombreTipo + " (interfaz)", encontrado);
            RevisarParametrosGenericos(tipo.GetGenericParameters(), nombreTipo);

            foreach (FieldDefinitionHandle campo in tipo.GetFields())
            {
                FieldDefinition definicion = _md.GetFieldDefinition(campo);
                if ((encontrado = definicion.DecodeSignature(_firmas, null)) != null)
                    Anotar(nombreTipo + "::" + _md.GetString(definicion.Name) + " (campo)", encontrado);
            }

            foreach (PropertyDefinitionHandle propiedad in tipo.GetProperties())
            {
                PropertyDefinition definicion = _md.GetPropertyDefinition(propiedad);
                if ((encontrado = DeLinqFirma(definicion.DecodeSignature(_firmas, null))) != null)
                    Anotar(nombreTipo + "::" + _md.GetString(definicion.Name) + " (propiedad)", encontrado);
            }

            foreach (EventDefinitionHandle evento in tipo.GetEvents())
            {
                EventDefinition definicion = _md.GetEventDefinition(evento);
                if ((encontrado = DeLinq(definicion.Type)) != null)
                    Anotar(nombreTipo + "::" + _md.GetString(definicion.Name) + " (evento)", encontrado);
            }

            foreach (MethodImplementationHandle implementacion in tipo.GetMethodImplementations())
            {
                MethodImplementation definicion = _md.GetMethodImplementation(implementacion);
                if ((encontrado = DeLinq(definicion.MethodDeclaration)) != null)
                    Anotar(nombreTipo + " (implementación explícita)", encontrado);
            }

            foreach (MethodDefinitionHandle metodo in tipo.GetMethods())
                RevisarMetodo(metodo, nombreTipo);
        }

        private void RevisarParametrosGenericos(GenericParameterHandleCollection parametros, string donde)
        {
            foreach (GenericParameterHandle parametro in parametros)
                foreach (GenericParameterConstraintHandle restriccion in _md.GetGenericParameter(parametro).GetConstraints())
                {
                    string? encontrado = DeLinq(_md.GetGenericParameterConstraint(restriccion).Type);
                    if (encontrado != null)
                        Anotar(donde + " (restricción genérica)", encontrado);
                }
        }

        private void RevisarMetodo(MethodDefinitionHandle manejador, string nombreTipo)
        {
            MethodDefinition metodo = _md.GetMethodDefinition(manejador);
            string donde = nombreTipo + "::" + _md.GetString(metodo.Name);
            string? encontrado = DeLinqFirma(metodo.DecodeSignature(_firmas, null));
            if (encontrado != null)
                Anotar(donde + " (firma)", encontrado);
            RevisarParametrosGenericos(metodo.GetGenericParameters(), donde);
            if (metodo.RelativeVirtualAddress == 0)
                return;

            MethodBodyBlock cuerpo = _pe.GetMethodBody(metodo.RelativeVirtualAddress);
            if (!cuerpo.LocalSignature.IsNil && (encontrado = DeLinq(cuerpo.LocalSignature)) != null)
                Anotar(donde + " (variable local)", encontrado, manejador, 0);
            foreach (ExceptionRegion region in cuerpo.ExceptionRegions)
                if (!region.CatchType.IsNil && (encontrado = DeLinq(region.CatchType)) != null)
                    Anotar(donde + " (catch)", encontrado, manejador, region.HandlerOffset);

            BlobReader il = cuerpo.GetILReader();
            while (il.RemainingBytes > 0)
            {
                int desplazamiento = il.Offset;
                int codigo = il.ReadByte();
                if (codigo == 0xFE)
                    codigo = 0xFE00 | il.ReadByte();
                int operando = TamanoOperando(codigo);
                if (operando == -4)
                {
                    int token = il.ReadInt32();
                    encontrado = codigo != 0x72 ? DeLinqTolerante(token) : null;
                    if (encontrado != null)
                        Anotar(donde, encontrado, manejador, desplazamiento);
                }
                else if (operando == -1)
                {
                    int casos = il.ReadInt32();
                    il.Offset += 4 * casos;
                }
                else
                    il.Offset += operando;
            }
        }

        // La localización es solo para el mensaje (lo que decide es la tabla de TypeRef): un token que no se puede leer no
        // para la tarea.
        private string? DeLinqTolerante(int token)
        {
            try
            {
                return DeLinq(MetadataTokens.EntityHandle(token));
            }
            catch (Exception excepcion) when (excepcion is InvalidCastException || excepcion is BadImageFormatException || excepcion is ArgumentException)
            {
                return null;
            }
        }

        // Bytes del operando de cada código de operación de ECMA-335 (-4: un token de metadatos; -1: switch).
        private static int TamanoOperando(int codigo)
        {
            switch (codigo)
            {
                case 0x27: case 0x28: case 0x29: case 0x6F: case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75:
                case 0x79: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: case 0x80: case 0x81: case 0x8C: case 0x8D:
                case 0x8F: case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0: case 0xFE06: case 0xFE07:
                case 0xFE15: case 0xFE16: case 0xFE1C:
                    return -4;
                case 0x45:
                    return -1;
                case 0x20: case 0x22: case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D: case 0x3E: case 0x3F:
                case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0xDD:
                    return 4;
                case 0x21: case 0x23:
                    return 8;
                case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: case 0x1F: case 0x2B: case 0x2C: case 0x2D:
                case 0x2E: case 0x2F: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35: case 0x36: case 0x37:
                case 0xDE: case 0xFE12: case 0xFE19:
                    return 1;
                case 0xFE09: case 0xFE0A: case 0xFE0B: case 0xFE0C: case 0xFE0D: case 0xFE0E:
                    return 2;
                default:
                    return 0;
            }
        }

        // Atributos: su constructor (el tipo del atributo) y los typeof de sus argumentos, que se guardan como nombres.
        private void RevisarAtributos()
        {
            ProveedorAtributos proveedor = new ProveedorAtributos(_md);
            foreach (CustomAttributeHandle manejador in _md.CustomAttributes)
            {
                CustomAttribute atributo = _md.GetCustomAttribute(manejador);
                string donde = "un atributo de " + Duenio(atributo.Parent);
                string? encontrado = _deLinq.Count > 0 ? DeLinq(atributo.Constructor) : null;
                if (encontrado != null)
                    Anotar(donde, encontrado);
                bool isConNombreDeTipo = IsAtributoConNombreDeTipo(TipoDelAtributo(atributo.Constructor));

                // Las apariciones de «System.Linq.» en los bytes del valor tienen que quedar explicadas por un argumento string
                // decodificado; si no (no se pudo decodificar, o una enumeración de otro ensamblado que no es int desalineó la
                // lectura y se saltó argumentos), se toma como un nombre de tipo: falla cerrado.
                string? tipoEnArgumento = null;
                int enBytes = ContarNombreLinq(_md.GetBlobBytes(atributo.Value));
                int explicadas = 0;
                try
                {
                    CustomAttributeValue<string> valor = atributo.DecodeValue(proveedor);
                    foreach (CustomAttributeTypedArgument<string> argumento in valor.FixedArguments)
                        tipoEnArgumento ??= TipoLinqEnArgumento(argumento.Type, argumento.Value, isConNombreDeTipo, ref explicadas);
                    foreach (CustomAttributeNamedArgument<string> argumento in valor.NamedArguments)
                        tipoEnArgumento ??= TipoLinqEnArgumento(argumento.Type, argumento.Value, isConNombreDeTipo, ref explicadas);
                }
                catch (Exception excepcion) when (excepcion is BadImageFormatException || excepcion is InvalidOperationException || excepcion is ArgumentException)
                {
                    explicadas = 0;
                }

                if (tipoEnArgumento == null && enBytes > explicadas)
                    tipoEnArgumento = "System.Linq (nombre de tipo en el valor)";

                if (tipoEnArgumento != null)
                    Anotar(donde + " (tipo en un argumento)", tipoEnArgumento);
            }
        }

        // Atributos cuyos argumentos string son nombres de tipo que el runtime, el trimmer y AOT resuelven: cuentan como una
        // referencia al tipo. Por el nombre completo, también si el ensamblado declara su propia copia.
        private static bool IsAtributoConNombreDeTipo(string? nombre) =>
            nombre == "System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute" || nombre == "System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute";

        // Nombre completo del tipo del atributo, desde su constructor (MemberRef sobre un TypeRef, o método propio).
        private string? TipoDelAtributo(EntityHandle constructor)
        {
            EntityHandle tipo = constructor.Kind == HandleKind.MemberReference
                ? _md.GetMemberReference((MemberReferenceHandle)constructor).Parent
                : constructor.Kind == HandleKind.MethodDefinition ? _md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType() : default;
            if (tipo.Kind == HandleKind.TypeReference)
            {
                TypeReference referencia = _md.GetTypeReference((TypeReferenceHandle)tipo);
                return _md.GetString(referencia.Namespace) + "." + _md.GetString(referencia.Name);
            }

            if (tipo.Kind != HandleKind.TypeDefinition || tipo.IsNil)
                return null;
            TypeDefinition definicion = _md.GetTypeDefinition((TypeDefinitionHandle)tipo);
            return _md.GetString(definicion.Namespace) + "." + _md.GetString(definicion.Name);
        }

        // explicadas: cuántas «System.Linq.» hay en los argumentos string que no son nombres de tipo. Con isConNombreDeTipo
        // (UnsafeAccessorType, DynamicDependency), un string que nombra un tipo de System.Linq o de debajo cuenta como referencia.
        private static string? TipoLinqEnArgumento(string tipo, object? valor, bool isConNombreDeTipo, ref int explicadas)
        {
            if (valor is ImmutableArray<CustomAttributeTypedArgument<string>> elementos)
            {
                foreach (CustomAttributeTypedArgument<string> elemento in elementos)
                {
                    string? encontrado = TipoLinqEnArgumento(elemento.Type, elemento.Value, isConNombreDeTipo, ref explicadas);
                    if (encontrado != null)
                        return encontrado;
                }

                return null;
            }

            if (!(valor is string nombre))
                return null;
            if (tipo != ProveedorAtributos.TipoSystemType)
            {
                if (isConNombreDeTipo && NombraTipoLinq(nombre))
                    return nombre + " (nombre de tipo en un string)";
                explicadas += ContarNombreLinq(System.Text.Encoding.UTF8.GetBytes(nombre));
                return null;
            }

            // Nombre serializado: «Espacio.Tipo+Anidado`1[[Argumento, Ensamblado]], Ensamblado, Version=...».
            int coma = nombre.IndexOf(',');
            string sinEnsamblado = coma >= 0 ? nombre.Substring(0, coma) : nombre;
            bool isLinq = sinEnsamblado.StartsWith("System.Linq.", StringComparison.Ordinal) || nombre.IndexOf("[System.Linq.", StringComparison.Ordinal) >= 0;
            return isLinq ? sinEnsamblado + " (typeof)" : null;
        }

        // Un nombre de tipo («Espacio.Tipo, Ensamblado», con argumentos genéricos entre corchetes) con algún tipo de System.Linq
        // o de debajo: «System.Linq.» al principio, tras un corchete o tras una coma, con espacios o sin ellos (el nombre del
        // ensamblado, «, System.Linq» sin punto detrás, no cuenta).
        private static bool NombraTipoLinq(string nombre)
        {
            int desde = 0;
            while (true)
            {
                int posicion = nombre.IndexOf("System.Linq", desde, StringComparison.Ordinal);
                if (posicion < 0)
                    return false;
                int fin = posicion + "System.Linq".Length;
                int previo = posicion - 1;
                while (previo >= 0 && char.IsWhiteSpace(nombre[previo]))
                    previo--;
                bool isInicioDeNombre = previo < 0 || nombre[previo] == '[' || nombre[previo] == ',';
                if (isInicioDeNombre && fin < nombre.Length && nombre[fin] == '.')
                    return true;
                desde = fin;
            }
        }

        private static int ContarNombreLinq(byte[] bytes)
        {
            byte[] buscado = System.Text.Encoding.UTF8.GetBytes("System.Linq.");
            int cuenta = 0;
            for (int i = 0; i + buscado.Length <= bytes.Length; i++)
            {
                int j = 0;
                while (j < buscado.Length && bytes[i + j] == buscado[j])
                    j++;
                if (j == buscado.Length)
                    cuenta++;
            }

            return cuenta;
        }

        private string Duenio(EntityHandle dueno)
        {
            switch (dueno.Kind)
            {
                case HandleKind.TypeDefinition:
                    return NombreTipo((TypeDefinitionHandle)dueno);
                case HandleKind.MethodDefinition:
                {
                    MethodDefinition metodo = _md.GetMethodDefinition((MethodDefinitionHandle)dueno);
                    return NombreTipo(metodo.GetDeclaringType()) + "::" + _md.GetString(metodo.Name);
                }
                case HandleKind.FieldDefinition:
                {
                    FieldDefinition campo = _md.GetFieldDefinition((FieldDefinitionHandle)dueno);
                    return NombreTipo(campo.GetDeclaringType()) + "::" + _md.GetString(campo.Name);
                }
                case HandleKind.PropertyDefinition:
                {
                    PropertyDefinition propiedad = _md.GetPropertyDefinition((PropertyDefinitionHandle)dueno);
                    return "la propiedad " + _md.GetString(propiedad.Name);
                }
                case HandleKind.EventDefinition:
                {
                    EventDefinition evento = _md.GetEventDefinition((EventDefinitionHandle)dueno);
                    return "el evento " + _md.GetString(evento.Name);
                }
                case HandleKind.Parameter:
                {
                    Parameter parametro = _md.GetParameter((ParameterHandle)dueno);
                    return "el parámetro " + _md.GetString(parametro.Name);
                }
                case HandleKind.AssemblyDefinition:
                    return "ensamblado";
                case HandleKind.ModuleDefinition:
                    return "módulo";
                default:
                    return dueno.Kind.ToString();
            }
        }
    }

    // Recorre una firma y devuelve el nombre del primer tipo de System.Linq que aparece en ella (o null).
    private sealed class ProveedorFirmas : ISignatureTypeProvider<string?, object?>
    {
        private readonly Comprobador _comprobador;

        public ProveedorFirmas(Comprobador comprobador) => _comprobador = comprobador;

        public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
        public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => _comprobador.DeLinq(handle);
        public string? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => _comprobador.DeLinq(handle);
        public string? GetSZArrayType(string? elementType) => elementType;
        public string? GetArrayType(string? elementType, ArrayShape shape) => elementType;
        public string? GetByReferenceType(string? elementType) => elementType;
        public string? GetPointerType(string? elementType) => elementType;
        public string? GetPinnedType(string? elementType) => elementType;
        public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments) => genericType ?? Comprobador.DeLinqLista(typeArguments);
        public string? GetGenericMethodParameter(object? genericContext, int index) => null;
        public string? GetGenericTypeParameter(object? genericContext, int index) => null;
        public string? GetFunctionPointerType(MethodSignature<string?> signature) => Comprobador.DeLinqFirma(signature);
        public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => modifier ?? unmodifiedType;
    }

    // Tipos de los argumentos de un atributo, por su nombre. Una enumeración de otro ensamblado se toma como int (el tipo
    // base de casi todas); si no lo es, la lectura se desalinea, y lo cubre la cuenta de «System.Linq.» en los bytes.
    private sealed class ProveedorAtributos : ICustomAttributeTypeProvider<string>
    {
        public const string TipoSystemType = "System.Type";
        private readonly MetadataReader _md;

        public ProveedorAtributos(MetadataReader md) => _md = md;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => TipoSystemType;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Nombre(reader, handle);

        // Con el tipo contenedor delante si está anidado: dos enumeraciones anidadas con el mismo nombre no se confunden.
        private static string Nombre(MetadataReader reader, TypeDefinitionHandle handle)
        {
            TypeDefinition tipo = reader.GetTypeDefinition(handle);
            TypeDefinitionHandle contenedor = tipo.GetDeclaringType();
            return contenedor.IsNil
                ? reader.GetString(tipo.Namespace) + "." + reader.GetString(tipo.Name)
                : Nombre(reader, contenedor) + "+" + reader.GetString(tipo.Name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference tipo = reader.GetTypeReference(handle);
            return reader.GetString(tipo.Namespace) + "." + reader.GetString(tipo.Name);
        }

        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type)
        {
            foreach (TypeDefinitionHandle manejador in _md.TypeDefinitions)
            {
                TypeDefinition tipo = _md.GetTypeDefinition(manejador);
                if (Nombre(_md, manejador) != type)
                    continue;
                foreach (FieldDefinitionHandle campo in tipo.GetFields())
                {
                    FieldDefinition definicion = _md.GetFieldDefinition(campo);
                    if ((definicion.Attributes & System.Reflection.FieldAttributes.Static) == 0)
                        return definicion.DecodeSignature(new ProveedorPrimitivo(), null);
                }
            }

            return PrimitiveTypeCode.Int32;
        }

        public bool IsSystemType(string type) => type == TipoSystemType;
    }

    // Solo para el campo value__ de una enumeración propia: su tipo primitivo.
    private sealed class ProveedorPrimitivo : ISignatureTypeProvider<PrimitiveTypeCode, object?>
    {
        public PrimitiveTypeCode GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public PrimitiveTypeCode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetSZArrayType(PrimitiveTypeCode elementType) => elementType;
        public PrimitiveTypeCode GetArrayType(PrimitiveTypeCode elementType, ArrayShape shape) => elementType;
        public PrimitiveTypeCode GetByReferenceType(PrimitiveTypeCode elementType) => elementType;
        public PrimitiveTypeCode GetPointerType(PrimitiveTypeCode elementType) => elementType;
        public PrimitiveTypeCode GetPinnedType(PrimitiveTypeCode elementType) => elementType;
        public PrimitiveTypeCode GetGenericInstantiation(PrimitiveTypeCode genericType, ImmutableArray<PrimitiveTypeCode> typeArguments) => genericType;
        public PrimitiveTypeCode GetGenericMethodParameter(object? genericContext, int index) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetGenericTypeParameter(object? genericContext, int index) => PrimitiveTypeCode.Int32;
        public PrimitiveTypeCode GetFunctionPointerType(MethodSignature<PrimitiveTypeCode> signature) => PrimitiveTypeCode.IntPtr;
        public PrimitiveTypeCode GetModifiedType(PrimitiveTypeCode modifier, PrimitiveTypeCode unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
