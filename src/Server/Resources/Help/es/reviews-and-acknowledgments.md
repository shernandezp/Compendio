# Revisiones y confirmaciones

Son las dos funciones que mantienen honesto un wiki. Una pregunta *«¿esto sigue siendo cierto?»*; la
otra, *«¿lo ha leído todo el mundo?»*.

## Responsable y ciclos de revisión

Abre **Revisión y responsable** en una página para indicar:

- **Responsable**: quién se encarga de que esta página siga siendo correcta. Recibe los avisos.
- **Revisar cada (días)**: cada cuánto hay que comprobarla. Déjalo vacío si no hay ciclo.

Al indicar un intervalo, el plazo empieza hoy. Cuando pasa la fecha, la página muestra *Esta página
necesita revisión*, se avisa al responsable, y aparece en su panel y en la pantalla de **Revisión
pendiente**.

### Confirmar una revisión

Lee la página, comprueba que sigue siendo correcta y pulsa **Confirmar revisión**. Eso reinicia el
plazo.

Si *no* es correcta, arréglala primero. Editar no borra el aviso por sí solo: sigues confirmando
cuando estés conforme.

### A qué ponerle intervalo

A todo lo que te costaría dinero o tiempo si estuviera mal: runbooks, listas de contactos,
procedimientos con dependencias externas, cualquier cosa que mencione un número de versión. El
material de referencia que no cambia no necesita ciclo, y un wiki donde todo está vencido enseña a
la gente a ignorar el aviso.

### La pantalla de Revisión pendiente

Lista todas las páginas vencidas que puedes ver, con su responsable y cuántos días de retraso
llevan. Las páginas sin responsable localizable aparecen como **Sin responsable**: son las que se
pudren en silencio, así que conviene repasarlas.

Puedes exportar la lista como CSV.

## Confirmaciones de lectura

Algunas páginas hay que *leerlas*, no solo publicarlas: una política de seguridad, un procedimiento
que cambia, un turno de guardia.

Activa **Requiere confirmación de lectura** en el mismo panel. A partir de ahí, a todo el que pueda
leer la página se le pide que confirme que lo ha hecho.

### Si eres quien lee

La página muestra *Debes confirmar que has leído esta página*. Léela y pulsa **He leído esta
página**. Tu confirmación registra **la versión exacta que leíste**, así que hay una respuesta real
a «¿con qué se conformó exactamente?».

Las páginas sin confirmar aparecen en tu panel, y pasan a estar vencidas si las dejas.

### Si eres el responsable de la página

El informe de **Confirmaciones** muestra quién ha confirmado y quién no, con el progreso, y se
exporta como CSV.

Cuando hagas un cambio lo bastante importante como para que la gente tenga que leerla de nuevo,
márcalo como **revisión importante**. Eso vuelve a preguntar a todo el mundo. Corregir una errata no
debería.
