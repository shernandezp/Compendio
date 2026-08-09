# Mantenimiento y copias de seguridad

**Administración → Estado** muestra la versión, cómo está instalado, la carpeta de contenido, el
número de páginas y carpetas, la vigilancia de archivos, el estado del índice de búsqueda, la cola de
indexación, el tamaño de la base de datos y del contenido, y cuándo se hizo la última copia.

## Copias de seguridad

**Crear una copia de seguridad** escribe un archivo con el contenido y la base de datos en la carpeta
de copias del servidor.

Si esta instancia tiene carpetas cifradas se te pide una **frase de contraseña de la copia**.
Protege la clave de cifrado dentro del archivo. **Guárdala en un lugar distinto de este servidor**:
la necesitarás para restaurar, y una frase guardada junto a la copia no protege nada.

*Nunca: ejecuta `compendio backup`* en **Última copia de seguridad** significa exactamente eso.
Prográmalo.

Como las páginas son archivos normales, una copia o sincronización de la carpeta cubre tu contenido.
Lo que **no** cubre es el historial de versiones, los usuarios, los grupos, las reglas de acceso ni
los registros de confirmación de lectura: eso vive en la base de datos. Respalda ambas cosas, que es
lo que hace el comando de copia.

Desde la línea de comandos:

```
compendio backup
compendio restore <archivo>
```

## Índice de búsqueda

El índice es una **caché**, nunca la fuente de verdad. Se reconstruye a partir de la carpeta de
contenido, y borrarlo no cuesta más que una reconstrucción.

- **Reconstruir el índice de búsqueda**: reconstrucción completa, en línea y por lotes. Mientras
  corre, los usuarios ven un aviso discreto de que los resultados pueden estar incompletos.
- **Volver a leer la carpeta de contenido**: reconcilia la imagen que tiene Compendio con lo que hay
  realmente en disco. Úsalo después de copiar archivos directamente, o si una página parece
  desajustada.

Desde la línea de comandos:

```
compendio reindex
compendio reindex --drop-secure
```

`--drop-secure` purga además el texto de las carpetas cifradas que se hubieran incluido en la
búsqueda.

## Comprobaciones de salud

- `/health` responde en cuanto el proceso está en pie. Es lo que debe usar el health check de un
  contenedor o un balanceador: deliberadamente no depende del índice, para que una reconstrucción no
  provoque el reinicio de una instancia sana.
- `/ready` informa del estado del índice, la profundidad de la cola y el progreso de una
  reconstrucción. Es información, no un veredicto.

## Diagnóstico

```
compendio doctor
```

Revisa la instalación e informa de lo que está mal, incluida una clave de cifrado que no se puede
descifrar, que es lo que significa **Clave: No disponible** en una carpeta cifrada.

## Espejo de git

**Administración → Integraciones** muestra el espejo de git: si está activado, la rama, cuándo envió
por última vez y el último error. Está desactivado salvo que se configure con `GitMirror:Enabled` y
una URL remota, y necesita `git` en el PATH del servidor; si falta git, el espejo se detiene y nada
más se ve afectado. **Enviar ahora** fuerza un envío.

Se avisa a los responsables cuando falla un envío del espejo, para que un espejo roto no falle en
silencio.
