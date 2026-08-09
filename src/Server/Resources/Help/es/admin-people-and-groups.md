# Personas y grupos

**Administración → Personas** y **Grupos**.

## Roles

Cada cuenta tiene un rol, y el rol es un **techo, no una concesión**:

| Rol | Techo |
|---|---|
| **Lectura** | Nunca puede escribir, diga lo que diga una carpeta |
| **Edición** | Puede escribir donde la carpeta lo permita |
| **Administración** | Todo, incluidas estas pantallas |

Esto importa cuando estás depurando «¿por qué esta persona no puede editar?». Mira primero el rol:
una regla de carpeta que concede Escritura a alguien con Lectura no hace nada. La pantalla de acceso
te lo dice directamente: *limitado por su rol de Lectura*.

## Añadir una persona

**Añadir una persona** pide usuario, contraseña, nombre visible, correo opcional y rol.

Compendio no envía correo, así que no hay enlace de invitación ni recuperación de contraseña por
cuenta propia. Tú pones la contraseña inicial y se la comunicas. Igualmente, alguien que se quede
fuera necesita que le **pongas una contraseña nueva**.

Dejar la contraseña en blanco al editar a una persona existente conserva la que ya tenía.

## Desactivar

Es preferible **Activa → no** a eliminar. Impide que la persona inicie sesión y conserva su nombre
junto a las ediciones que hizo, así que el historial sigue siendo legible.

Compendio no te dejará desactivar ni degradar al último administrador activo. Si esa es la cuenta de
la que te has quedado fuera, la recuperación es un comando en el servidor.

## Grupos

Un grupo es un conjunto de personas con nombre. Concede el acceso al grupo, no a las personas: así,
incorporar a alguien al departamento es una sola acción en lugar de un repaso a todas las carpetas.

No se admiten grupos anidados: un grupo contiene personas, no otros grupos. Es deliberado; los
grupos anidados hacen mucho más difícil responder a «¿por qué esta persona ve esto?».

**Gestionar miembros** edita la pertenencia. El número de miembros se muestra junto a cada grupo.

## Estructura recomendada

Crea grupos que reflejen cómo decide el acceso vuestra organización de verdad: normalmente
departamentos, más uno o dos transversales como *guardia*. Concede el acceso a las carpetas a esos
grupos. Reserva las concesiones por persona para excepciones auténticas, y cuenta con tener muy
pocas.

## Registro de auditoría

**Administración → Registro de auditoría** recoge quién hizo qué y cuándo: cambios de rol, de
acceso, de carpetas cifradas. Es el primer sitio donde mirar cuando algo no está como lo dejaste.
