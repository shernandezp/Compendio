# Acceso y permisos

**Administración → Acceso**, o la pantalla de acceso de cualquier carpeta.

Los permisos se definen **por carpeta**, y las páginas heredan de la carpeta en la que están. No hay
reglas por página: es lo que mantiene respondible la pregunta «¿quién puede ver esto?».

## Los cuatro niveles

| Nivel | Puede |
|---|---|
| **Sin acceso** | Nada. La carpeta es invisible. |
| **Lectura** | Leer las páginas |
| **Escritura** | Leer y editar las páginas |
| **Gestión** | Leer, editar y cambiar las reglas de acceso de esta carpeta |

Recuerda el techo del rol: alguien con rol de Lectura al que concedas Escritura sigue sin poder
escribir.

## Herencia

Por defecto una carpeta **hereda de su carpeta superior**, y el primer nivel hereda el valor por
defecto de la instancia elegido durante la instalación, normalmente «cualquiera que inicie sesión
puede leer».

Para restringir una carpeta, cámbiala a **Restringido: solo las personas y grupos de abajo** y
enumera quién entra. Eso corta la herencia en ese punto.

## No hay reglas de denegación

Es deliberado, y es lo más importante que hay que entender aquí.

No puedes quitarle el acceso a alguien que lo tiene por herencia. Para restringir una carpeta,
**cortas la herencia y enumeras quién entra**. Las reglas de denegación de otros sistemas producen
conjuntos de permisos sobre los que nadie puede razonar —«lee aquí, denegado allá, pero es miembro
de dos grupos, uno de los cuales…»— y su forma de fallar es la sobreexposición silenciosa.

## Comprueba lo que has hecho

La **vista previa del acceso efectivo** responde a *¿qué puede hacer aquí esta persona?* para alguien
concreto, y te dice **por qué**:

- *porque es administrador*
- *limitado por su rol de Lectura*
- *por el valor por defecto de la instancia*
- *heredado de una carpeta superior*
- *porque esta carpeta está cifrada y solo los administradores pueden modificarla*

Úsala después de cada cambio que no sea obvio. Es más rápido que razonarlo, y usa el mismo evaluador
que el resto del producto, así que no puede discrepar de la realidad.

## Qué oculta realmente una restricción

Una página restringida es invisible en todas partes, no solo en el árbol: en los resultados de
búsqueda y en su recuento, en el selector de Ctrl-K, en las sugerencias de `[[enlace]]` del editor,
en las páginas que la enlazan, en los recuentos de etiquetas, en «actualizado recientemente», en las
fuentes del asistente de IA y en las exportaciones. Nadie puede enterarse de que existe por ninguna
de esas vías.

Por eso una página que falta responde *«no existe, o no tienes acceso»* en lugar de un error de
permisos: un 403 confirmaría que está ahí.

## Mover una carpeta

Las reglas de acceso se mueven con la carpeta. Una página nunca queda expuesta un instante por estar
en tránsito.
