# Historial y versiones

Todos los cambios de todas las páginas quedan registrados. Nada de lo que hagas en Compendio destruye
el texto anterior.

## Ver el historial

**Historial** en cualquier página lista sus versiones, de la más reciente a la más antigua. Cada
entrada indica cuándo ocurrió, quién lo hizo y por qué:

| Etiqueta | Qué pasó |
|---|---|
| **Editada aquí** | Alguien guardó desde el editor |
| **Editada en la carpeta de contenido** | Se cambió el archivo directamente en el servidor |
| **Movida** | Se movió o se renombró la página |
| **Eliminada** | Se borró la página; su historial sobrevive |
| **Restaurada** | Se recuperó una versión anterior |
| **Formato ordenado** | Una página escrita fuera de Compendio se normalizó, una sola vez |

Un cambio hecho en la carpeta de contenido **no tiene autor**: el sistema de archivos no registra
quién lo escribió. Es lo esperable en una instancia donde también se editan archivos directamente.

## Comparar

Selecciona dos versiones y pulsa **Comparar**. Ves las diferencias de dos maneras:

- **Código**: línea a línea, con exactamente lo que cambió en el texto.
- **Con formato**: la página tal como se veía, con lo añadido y lo eliminado marcado.

Código va mejor para un cambio pequeño de redacción; con formato, para ver una página
reestructurada.

## Restaurar

**Restaurar esta versión** recupera una versión antigua. No borra nada: restaurar *añade una versión
nueva* cuyo contenido es el texto antiguo. Así que una restauración también se puede deshacer, y el
registro de lo ocurrido sigue completo.

## Páginas eliminadas

Al eliminar una página se borra el archivo pero se conserva el historial. Un administrador puede
recuperarla.

## Dónde está esto realmente

El texto actual de cada página es un archivo Markdown en la carpeta de contenido: puedes abrirlo,
copiarlo, respaldarlo o poner la carpeta en git. El historial de versiones vive en la base de datos
de Compendio, junto a él. Las copias de seguridad cubren ambas cosas; consulta la guía de
administración.
