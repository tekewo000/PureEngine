#version 450
layout(binding=0) uniform sampler2D atlas;
layout(location=0) in vec2 texCoord;
layout(location=1) in vec4 tint;
layout(location=2) in vec4 bounds;
layout(location=3) in vec2 logicalPosition;
layout(location=0) out vec4 result;
void main() {
    if(any(lessThan(logicalPosition,bounds.xy)) || any(greaterThanEqual(logicalPosition,bounds.zw))) discard;
    result=texture(atlas,texCoord)*vec4(tint.rgb*tint.a,tint.a);
}
