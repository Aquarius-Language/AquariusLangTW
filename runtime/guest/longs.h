/* 長整數 / long: 64-bit integers (tag LONG). A long's two's complement bits fill Value.number (the same 8 bytes a double
 * uses, read with memcpy, never as a double). The rules widen the 32-bit ones: + - * and negation wrap around (i64),
 * / and % truncate and fail on a zero divisor, the minimum divided by -1 wraps to itself, shift counts use their low 6
 * bits. An int with a long gives a long; a long with a float or double gives a double. Comparisons are exact: a long
 * and a double compare their exact values. Included by runtime.c after number() and integer(). */
#define LONG_CODE 6u /* the annotation type code of 長整數 (annotation codes keep a type in 4 bits; the tag is 17) */
static int64_t long_of(Value v) { int64_t n;memcpy(&n,&v.number,8);return n; }
static Value long_value(int64_t n) { Value v=value(LONG,0,0);memcpy(&v.number,&n,8);return v; }
static Value long_wrap(uint64_t n) { return long_value((int64_t)n); }
/* Any number: an int, float, double or long. numeric() stays the three double-payload types. */
static int any_number(Value v) { return numeric(v)||v.tag==LONG; }
/* A number's value as a double (a long rounds to nearest). */
static double number_of(Value v) { return v.tag==LONG?(double)long_of(v):v.number; }
static int64_t saturate_long(double n) { return n!=n?0:n<=-9223372036854775808.0?INT64_MIN:n>=9223372036854775808.0?INT64_MAX:(int64_t)n; }
static Value long_to_int(int64_t n) { return value(INT,0,(double)(n<INT32_MIN?INT32_MIN:n>INT32_MAX?INT32_MAX:n)); }
/* A number converted to tag INT, FLOAT, DOUBLE or LONG: to the integers truncating toward zero and saturating (NaN 0). */
static Value convert_number(Value v,u32 tag) {
    if(v.tag==LONG){int64_t n=long_of(v);return tag==LONG?v:tag==INT?long_to_int(n):tag==FLOAT?value(FLOAT,0,(double)(float)n):value(DOUBLE,0,(double)n);}
    if(tag==LONG)return long_value(v.tag==INT?(int64_t)v.number:saturate_long(v.number));
    return number(v.number,tag);
}
/* The annotation type of a code's low 4 bits as a value tag (長整數's code 6 is the tag LONG). */
static u32 code_tag(u32 code) { u32 t=code&15u;return t==LONG_CODE?LONG:t; }
/* Widens a number in place to an annotation's number type, as typed variables do: int to long, float and double;
   long and float to the floating types; float to double. */
static int widen_number(Value*v,u32 want) {
    if(v->tag==INT&&want==LONG){*v=long_value((int64_t)v->number);return 1;}
    if(v->tag==LONG&&(want==FLOAT||want==DOUBLE)){*v=convert_number(*v,want);return 1;}
    if(numeric(*v)&&want>=FLOAT&&want<=DOUBLE&&v->tag<want){*v=number(v->number,want);return 1;}
    return 0;
}
/* The name of a scalar type (a tag, or 長整數's code) in messages. */
static const char *scalar_type_name(u32 t) { static const char *const names[]={"","int","float","double","bool","string","long"};return t==LONG?"long":t<=LONG_CODE?names[t]:"?"; }
/* A long and a double compared exactly: -1, 0 or 1, or 2 when the double is NaN. */
static int compare_long_double(int64_t l,double d) {
    if(d!=d)return 2;
    if(d>=9223372036854775808.0)return -1;
    if(d<-9223372036854775808.0)return 1;
    double t=__builtin_trunc(d);int64_t i=(int64_t)t;
    if(l!=i)return l<i?-1:1;
    return d>t?-1:d<t?1:0;
}
/* The exact order of two numbers of any types: -1, 0 or 1, or 2 when either is NaN. */
static int compare_numbers(Value a,Value b) {
    if(a.tag==LONG&&b.tag==LONG){int64_t x=long_of(a),y=long_of(b);return x<y?-1:x>y;}
    if(a.tag==LONG)return compare_long_double(long_of(a),b.number);
    if(b.tag==LONG){int c=compare_long_double(long_of(b),a.number);return c==2?2:-c;}
    return a.number!=a.number||b.number!=b.number?2:a.number<b.number?-1:a.number>b.number;
}
static int is_nan_number(Value v) { return v.tag!=LONG&&v.number!=v.number; }
/* binary() for operands of which at least one is a long (op as in binary; 0 when the operands are not numbers). With an
   int or long the operation is 64-bit; with a float or double it is a double operation. A compound assignment keeps
   the left operand's type, converting like the conversions do. */
static Value long_binary(Frame*f,int op,Value a,Value b,int compound,int*handled) {
    *handled=any_number(a)&&any_number(b);
    if(!*handled)return empty;
    if(op>=4&&op<=9){int c=compare_numbers(a,b);
        switch(op){case 4:return value(BOOL,0,c==-1);case 5:return value(BOOL,0,c==1);case 6:return value(BOOL,0,c==-1||c==0);
        case 7:return value(BOOL,0,c==1||c==0);case 8:return value(BOOL,0,c==0);default:return value(BOOL,0,c!=0);}}
    if(op==10||op==11){*handled=0;return empty;}
    if(a.tag==FLOAT||a.tag==DOUBLE||b.tag==FLOAT||b.tag==DOUBLE){
        if(op>=13){*handled=0;return empty;}
        double x=number_of(a),y=number_of(b),r=op==0?x+y:op==1?x-y:op==2?x*y:op==3?x/y:rt_remainder(x,y);
        return convert_number(value(DOUBLE,0,r),compound?a.tag:DOUBLE);
    }
    int64_t x=a.tag==LONG?long_of(a):(int64_t)a.number,y=b.tag==LONG?long_of(b):(int64_t)b.number;uint64_t r;u32 s=(u32)y&63u;
    switch(op){
    case 0:r=(uint64_t)x+(uint64_t)y;break;
    case 1:r=(uint64_t)x-(uint64_t)y;break;
    case 2:r=(uint64_t)x*(uint64_t)y;break;
    case 3:if(!y){failure(f,"Integer division by zero / 整數不能除以零。");return empty;}r=y==-1?0-(uint64_t)x:(uint64_t)(x/y);break;
    case 12:if(!y){failure(f,"Integer remainder by zero / 整數不能對零取餘數。");return empty;}r=y==-1?0:(uint64_t)(x%y);break;
    case 13:r=(uint64_t)(x&y);break;
    case 14:r=(uint64_t)(x|y);break;
    case 15:r=(uint64_t)(x^y);break;
    case 16:r=(uint64_t)x<<s;break;
    case 17:r=(uint64_t)(x>>s);break;
    default:r=(uint64_t)x>>s;break;
    }
    return compound&&a.tag==INT?long_to_int((int64_t)r):long_wrap(r);
}
