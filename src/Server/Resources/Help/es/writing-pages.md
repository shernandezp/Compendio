# Escribir páginas

**Nunca tienes que escribir Markdown.** El editor es un editor con formato: lo que ves es lo que
será la página. El archivo de debajo es Markdown, que es lo que lo hace legible para siempre, pero
eso es asunto del archivo, no tuyo.

## Crear una página

Usa **Página nueva** en el árbol, en la carpeta que le corresponda. Se te pregunta de qué trata la
página; eso pasa a ser su título. También puedes elegir una **plantilla**:

| Plantilla | Para |
|---|---|
| Página en blanco | Cualquier cosa |
| Procedimiento | Instrucciones paso a paso |
| Runbook | Qué hacer cuando algo se rompe |
| Política | Normas que hay que cumplir |
| Notas de reunión | Decisiones y tareas |

Las plantillas son un punto de partida, no una obligación. Borra lo que no necesites.

## Escribir

Pulsa **/** en cualquier punto del editor para ver un menú de cosas que insertar: encabezados,
listas, listas de tareas, citas, código, bloques de código, enlaces, imágenes, tablas, separadores y
diagramas. La barra de herramientas ofrece lo mismo.

Los atajos de siempre funcionan: **Ctrl-B** para negrita, **Ctrl-I** para cursiva.

Al pegar desde Word, un navegador o un correo se conserva el formato y se convierte. Si quieres solo
el texto, usa **Pegar sin formato**.

### Enlazar a otra página

Escribe `[[` y empieza a teclear el nombre de una página. Solo se sugieren las que puedes leer, y el
enlace sigue siendo válido si después se mueve o se renombra la página de destino.

### Adjuntos

**Añadir un archivo** adjunta un documento, una imagen o un comprimido a la página. Los nombres de
los adjuntos se pueden buscar.

## Guardar

Pulsa **Guardar**. Si intentas salir con cambios sin guardar, se te avisa antes.

Si el navegador o el ordenador se cae a mitad de una edición, tu texto se conserva localmente y se
te ofrece la próxima vez que abras esa página: *Se ha recuperado un borrador sin guardar*. Puedes
aceptarlo o descartarlo.

### Si otra persona guardó mientras escribías

Aparece una pantalla de **conflicto** con tu versión y la suya lado a lado. No se pierde nada ni se
sobrescribe nada en silencio: tú eliges qué conservar. Resolver un conflicto necesita una pantalla
razonablemente ancha, así que en el móvil se te pedirá que lo termines en un ordenador.

### Si te dice que no puedes guardar

*«No tienes permiso para guardar aquí»* significa que puedes leer esta carpeta pero no escribir en
ella. Tu texto sigue en el editor: cópialo a algún sitio antes de salir y pide acceso de escritura a
un administrador.

## Editar el archivo directamente

Como cada página es un archivo Markdown real, también puedes editarla en VS Code o en cualquier
editor, sobre la carpeta de contenido del servidor. Compendio se da cuenta, registra una versión y
muestra la página como *actualizada en la carpeta de contenido*. La primera vez que guardes aquí una
página así, se ordenará su formato una sola vez.
