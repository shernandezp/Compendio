# Organizar las páginas

## Carpetas

Las carpetas son carpetas reales en el servidor, y son además la unidad de control de acceso: quién
puede leer o escribir se decide por carpeta. Eso hace que la estructura merezca algo de reflexión.

Una estructura que funciona en casi todas las organizaciones es una carpeta de primer nivel por
área —*IT*, *RRHH*, *Procedimientos*— con páginas dentro. Anidar mucho tiende a esconder las cosas.

Usa **Carpeta nueva** en el árbol. **Eliminar esta carpeta** la borra con todo lo que contiene;
antes se te dice cuántas páginas son, y el historial de todas ellas se conserva y se puede
restaurar.

## Mover y renombrar

**Mover o renombrar** hace las dos cosas: cambiar el nombre, la carpeta, o ambas a la vez.

- Los nombres no pueden contener `/` ni `\`; elige la carpeta de destino en su lugar.
- Se rechazan los caracteres que Windows no admite (`< > : " | ? *`, y el punto o espacio final),
  porque la carpeta de contenido tiene que funcionar en cualquier sistema operativo.
- Las carpetas donde solo tienes lectura se marcan como *solo lectura* y no se pueden elegir como
  destino.

Los enlaces a una página movida siguen funcionando.

## Títulos

**Cambiar el título** cambia lo que ve todo el mundo. El nombre del archivo se queda como está, así
que los títulos pueden llevar acentos y puntuación mientras los nombres de archivo siguen siendo
simples y portables.

## Etiquetas

Las etiquetas atraviesan las carpetas: una página vive en una sola carpeta pero puede llevar varias
etiquetas. Van bien para lo que es cierto de páginas repartidas por muchos sitios: `seguridad`,
`incorporacion`, `sql-server`.

La pantalla de **Etiquetas** las lista todas con su recuento. Mantén el vocabulario corto: veinte
etiquetas usadas con constancia valen más que doscientas usadas una vez cada una.

## Enlaces entre páginas

Escribe `[[` en el editor para enlazar a otra página. Cada página muestra **Enlazada desde**, con
las páginas que enlazan a ella, que suele ser como descubres que un procedimiento se referencia
desde tres sitios que no conocías.

Los enlaces a páginas que quien lee no puede ver simplemente no se resuelven para esa persona, así
que la lista de enlaces nunca se convierte en una forma de descubrir nombres de páginas
restringidas.

## Responsable

Cada página puede tener un **responsable**: la persona encargada de que siga siendo correcta. Los
responsables reciben los avisos de revisión. Una página sin responsable funciona igual; solo
significa que no se le está preguntando a nadie. Consulta *Revisiones y confirmaciones*.

## Dos idiomas

Las traducciones viven junto al original en lugar de en un árbol paralelo: `teletrabajo.md` y
`teletrabajo.es.md`. Ambas aparecen como un solo documento con un selector de idioma. Consulta
*Leer una página*.
