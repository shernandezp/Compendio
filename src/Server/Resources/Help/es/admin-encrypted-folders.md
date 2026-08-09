# Carpetas cifradas

**Administración → Carpetas cifradas.** Cifra en disco los archivos de la carpeta que elijas.

## Qué protege, y qué no

**Protege frente a:** un disco robado, un archivo de copia de seguridad, una carpeta mal
sincronizada y un futuro espejo en git.

**No protege frente a:** un administrador de este servidor. Y **no oculta los nombres de carpetas ni
de archivos**, solo el contenido.

Si tu preocupación es «esta persona de la empresa no debería leer esto», ese es un trabajo para las
reglas de acceso, no para el cifrado. El cifrado es para «estos datos salieron del edificio en un
disco».

## El coste: se suspende el principio de archivos primero

La promesa habitual de Compendio es que cada página es un archivo Markdown que puedes abrir con
cualquier editor. Dentro de una carpeta cifrada eso deja de ser cierto: `runbook.md.enc` no se abre
en VS Code.

Para editar uno de esos archivos directamente se usan `compendio secure export` y
`compendio secure import` en el servidor. Sopesa esto antes de cifrar una carpeta en la que la gente
trabaja a diario.

Solo los administradores pueden modificar páginas dentro de una carpeta cifrada. Cualquiera con
acceso de lectura las lee con normalidad desde la interfaz web.

## Dos interruptores que conviene entender

### Incluir estas páginas en la búsqueda

**Desactivado por defecto, y piénsalo antes de activarlo.** Al activarlo, el texto de las páginas se
guarda *sin cifrar* en el índice de búsqueda, dentro de `compendio.db`. Cualquiera con ese archivo
puede leerlo, lo que debilita bastante aquello por lo que activaste el cifrado.

Si lo dejas desactivado, las páginas simplemente no aparecen en la búsqueda para nadie.

### Permitir que las funciones de IA lean estas páginas

También desactivado por defecto. Activarlo significa que el contenido de esta carpeta se envía al
endpoint de IA configurado cada vez que alguien usa una acción de IA sobre una página de dentro. La
confirmación nombra el endpoint. Si lo dejas desactivado, el asistente no puede leer estas páginas y
nunca las citará.

## Claves y copias de seguridad

El estado de la carpeta indica si la clave es **Legible** o **No disponible**. No disponible
significa que la clave no se puede descifrar en esta máquina: ejecuta `compendio doctor` en el
servidor. El contenido no cifrado se sigue sirviendo con normalidad mientras tanto.

**Las copias de seguridad de una instancia con carpetas cifradas piden una frase de contraseña.**
Protege la clave de cifrado dentro del archivo. **Guárdala en un lugar distinto de este servidor**,
porque la necesitarás para restaurar, y una frase de contraseña guardada junto a lo que protege no
protege nada.

No se permite anidar una carpeta cifrada dentro de otra.
