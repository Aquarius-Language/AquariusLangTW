/* Builtin IDs 60..96: the portable Math library. Included after texts.h.
 * Every function computes in the guest itself: the Wasm instructions where Wasm has one (sqrt, floor, ceil, trunc, min,
 * max, abs, all correctly rounded), and the vendored musl libm (third_party/musl) for the transcendental functions, so
 * results are bit-identical on every target. Results follow IEEE 754 (log(0) is -Infinity, sqrt(-1) is NaN): no domain
 * errors. Number types are kept like arithmetic: an integer stays an integer and a float a float where the result can
 * be one; transcendental results are double, or float when the (widest) argument is a float. */
#include "third_party/musl/shim/math.h"
/* The Math library: ids, bilingual names and arities live only here; the host never implements or lists them. */
#define MATH_FIRST_ID 60u
#define MATH_COUNT 37u
static const uint16_t *const math_module_names[2]={u"Math",u"數學"};
static const uint16_t *const math_names[MATH_COUNT][2]={{u"Abs",u"絕對值"},{u"Sign",u"正負號"},{u"Min",u"最小值"},{u"Max",u"最大值"},{u"Clamp",u"限制範圍"},
    {u"Floor",u"向下取整"},{u"Ceil",u"向上取整"},{u"Round",u"四捨五入"},{u"Trunc",u"截斷"},
    {u"FloorInt",u"向下取整數"},{u"CeilInt",u"向上取整數"},{u"RoundInt",u"四捨五入取整數"},{u"TruncInt",u"截斷取整數"},
    {u"Sqrt",u"平方根"},{u"Cbrt",u"立方根"},{u"Hypot",u"斜邊長"},{u"Pow",u"次方"},{u"Exp",u"指數"},{u"Log",u"對數"},{u"Log2",u"以二為底對數"},{u"Log10",u"以十為底對數"},
    {u"Sin",u"正弦"},{u"Cos",u"餘弦"},{u"Tan",u"正切"},{u"Asin",u"反正弦"},{u"Acos",u"反餘弦"},{u"Atan",u"反正切"},{u"Atan2",u"雙參數反正切"},
    {u"Sinh",u"雙曲正弦"},{u"Cosh",u"雙曲餘弦"},{u"Tanh",u"雙曲正切"},{u"IsNaN",u"是非數"},{u"IsFinite",u"是有限數"},{u"Fmod",u"浮點餘數"},
    {u"Random",u"亂數"},{u"RandomInt",u"亂數整數"},{u"Seed",u"亂數種子"}};
static const unsigned char math_minimum[MATH_COUNT]={1,1,2,2,3,1,1,1,1,1,1,1,1,1,1,2,2,1,1,1,1,1,1,1,1,1,1,2,1,1,1,1,1,2,0,2,1};
static const unsigned char math_maximum[MATH_COUNT]={1,1,2,2,3,1,1,1,1,1,1,1,1,1,1,2,2,1,1,1,1,1,1,1,1,1,1,2,1,1,1,1,1,2,0,2,1};
/* Constants of the module (double values), declared beside the functions. */
#define MATH_CONSTANTS 5u
static const uint16_t *const math_constants[MATH_CONSTANTS][2]={{u"PI",u"圓周率"},{u"E",u"自然常數"},{u"TAU",u"二倍圓周率"},{u"Infinity",u"無限大"},{u"NaN",u"非數"}};
static const double math_constant_values[MATH_CONSTANTS]={3.141592653589793,2.718281828459045,6.283185307179586,__builtin_inf(),__builtin_nan("")};
enum { MATH_ABS, MATH_SIGN, MATH_MIN, MATH_MAX, MATH_CLAMP, MATH_FLOOR, MATH_CEIL, MATH_ROUND, MATH_TRUNC, MATH_FLOOR_INT, MATH_CEIL_INT,
    MATH_ROUND_INT, MATH_TRUNC_INT, MATH_SQRT, MATH_CBRT, MATH_HYPOT, MATH_POW, MATH_EXP, MATH_LOG, MATH_LOG2, MATH_LOG10, MATH_SIN, MATH_COS,
    MATH_TAN, MATH_ASIN, MATH_ACOS, MATH_ATAN, MATH_ATAN2, MATH_SINH, MATH_COSH, MATH_TANH, MATH_IS_NAN, MATH_IS_FINITE, MATH_FMOD,
    MATH_RANDOM, MATH_RANDOM_INT, MATH_SEED };
static Scope *math_library(void) {
    Scope*s=scope(0);s->captured=1;for(u32 i=0;i<MATH_COUNT;i++)for(u32 j=0;j<2;j++)declare(s,utf16(math_names[i][j]),value(BUILTIN,MATH_FIRST_ID+i,0));
    for(u32 i=0;i<MATH_CONSTANTS;i++)for(u32 j=0;j<2;j++)declare(s,utf16(math_constants[i][j]),value(DOUBLE,0,math_constant_values[i]));return s;
}
/* Rounds half away from zero (Math.Round, 四捨五入): x - trunc(x) is exact, and -0.4 rounds to -0. */
static double round_away(double x) { double t=__builtin_trunc(x);return __builtin_fabs(x-t)>=0.5?t+__builtin_copysign(1.0,x):t; }
/* Min and Max of two doubles like the Wasm instructions: NaN if either is NaN, and -0 below +0. */
static double math_min(double x,double y) { return __builtin_wasm_min_f64(x,y); }
static double math_max(double x,double y) { return __builtin_wasm_max_f64(x,y); }

/* Math.Random: PCG32 (O'Neill, pcg32_srandom_r / pcg32_random_r), the same sequence on every target for a given seed. Until
   Math.Seed is called it is seeded once from the host clock (目前時間), or from a fixed seed where there is none. */
static uint64_t random_state, random_increment;
static int random_seeded;
static uint32_t random_next(void) {
    uint64_t old=random_state;random_state=old*6364136223846793005ull+random_increment;
    uint32_t shifted=(uint32_t)(((old>>18)^old)>>27),rotation=(uint32_t)(old>>59);return (shifted>>rotation)|(shifted<<((-rotation)&31));
}
static void random_seed(uint64_t seed) { random_state=0;random_increment=(0xda3e39cb94b95bdbull<<1)|1u;random_next();random_state+=seed;random_next();random_seeded=1; }
static void random_ensure(Frame*f) {
    if(random_seeded)return;
    Value clock=empty,now=empty;uint64_t seed=0x853c49e6748fea9bull;
    if(f&&!host_service(0,f->context,0,address(utf16(u"currentTime")),0,0,address(&clock))&&clock.tag==NATIVE
        &&host_service(1,f->context,clock.ref,0,0,0,address(&now))!=1&&numeric(now)){uint64_t bits;memcpy(&bits,&now.number,8);seed^=bits;}
    random_seed(seed);
}
/* A double in [0, 1) with 53 random bits. */
static double random_double(void) { uint32_t a=random_next()>>5,b=random_next()>>6;return (a*67108864.0+b)*(1.0/9007199254740992.0); }

/* The double result of a floating Math function on doubles. Native code calls it for the functions Wasm has no
   instruction for (WasmNativeFunction.MathCall); the generic call below converts to the result type. */
double rt_math(int id,double x,double y) {
    switch(id){
    case MATH_SQRT:return __builtin_sqrt(x);
    case MATH_CBRT:return cbrt(x);
    case MATH_HYPOT:return hypot(x,y);
    case MATH_POW:return pow(x,y);
    case MATH_EXP:return exp(x);
    case MATH_LOG:return log(x);
    case MATH_LOG2:return log2(x);
    case MATH_LOG10:return log10(x);
    case MATH_SIN:return sin(x);
    case MATH_COS:return cos(x);
    case MATH_TAN:return tan(x);
    case MATH_ASIN:return asin(x);
    case MATH_ACOS:return acos(x);
    case MATH_ATAN:return atan(x);
    case MATH_ATAN2:return atan2(x,y);
    case MATH_SINH:return sinh(x);
    case MATH_COSH:return cosh(x);
    case MATH_TANH:return tanh(x);
    case MATH_FMOD:return fmod(x,y);
    case MATH_ROUND:return round_away(x);
    default:return __builtin_nan("");
    }
}
/* The % operator on numbers that are not both integers: the exact IEEE remainder of truncating division (C fmod: the
   dividend's sign, 1e17 % 3 is 1, -0.0 % 3 is -0, 5 % Infinity is 5). The generic runtime, generic compiled code and
   native code all call this one function. */
double rt_remainder(double x,double y) { return fmod(x,y); }
static Value math_call(Frame*f,u32 id,Value*args,u32 n) {
    if(id<MATH_FIRST_ID||id>=MATH_FIRST_ID+MATH_COUNT||n<math_minimum[id-MATH_FIRST_ID]||n>math_maximum[id-MATH_FIRST_ID]){failure(f,"Incorrect Math argument count.");return empty;}
    u32 k=id-MATH_FIRST_ID,tag=INT;
    for(u32 i=0;i<n;i++){
        if(!numeric(args[i])){Text*t=concat(concat(ascii("Math."),utf16(math_names[k][0])),ascii(" requires numbers / 數學."));
            fail(f,concat(concat(concat(t,utf16(math_names[k][1])),ascii(" 需要數字: ")),ascii(type_name(args[i].tag))));return empty;}
        if(args[i].tag>tag)tag=args[i].tag;
    }
    double x=n?args[0].number:0,y=n>1?args[1].number:0;
    switch(k){
    case MATH_ABS:return tag==INT?integer(x<0?-(int64_t)x:(int64_t)x):number(__builtin_fabs(x),tag);
    case MATH_SIGN:return number(x>0?1:x<0?-1:x,tag);
    case MATH_MIN:return number(math_min(x,y),tag);
    case MATH_MAX:return number(math_max(x,y),tag);
    case MATH_CLAMP:return number(math_min(math_max(x,y),args[2].number),tag);
    case MATH_FLOOR:return number(__builtin_floor(x),tag);
    case MATH_CEIL:return number(__builtin_ceil(x),tag);
    case MATH_ROUND:return number(round_away(x),tag);
    case MATH_TRUNC:return number(__builtin_trunc(x),tag);
    case MATH_FLOOR_INT:return number(__builtin_floor(x),INT);
    case MATH_CEIL_INT:return number(__builtin_ceil(x),INT);
    case MATH_ROUND_INT:return number(round_away(x),INT);
    case MATH_TRUNC_INT:return number(x,INT);
    case MATH_IS_NAN:return value(BOOL,0,x!=x);
    case MATH_IS_FINITE:return value(BOOL,0,x-x==0);
    case MATH_RANDOM:random_ensure(f);return value(DOUBLE,0,random_double());
    case MATH_RANDOM_INT:{
        if(args[0].tag!=INT||args[1].tag!=INT||y<x){failure(f,"Math.RandomInt requires integers low <= high / 數學.亂數整數 需要整數且下限不大於上限。");return empty;}
        random_ensure(f);uint32_t range=(uint32_t)((int64_t)y-(int64_t)x);if(!range)return args[0];
        /* Unbiased: rejects the 2^32 mod range lowest draws. */
        uint32_t threshold=(0u-range)%range,r;do r=random_next();while(r<threshold);
        return integer((int64_t)x+r%range);
    }
    case MATH_SEED:
        if(args[0].tag!=INT){failure(f,"Math.Seed requires an integer / 數學.亂數種子 需要整數。");return empty;}
        random_seed((uint64_t)(int64_t)x);return empty;
    default:return number(rt_math((int)k,x,y),tag==FLOAT?FLOAT:DOUBLE);
    }
}
