import numpy as np, itertools
d2r=np.pi/180
def radec(ra,dec): ra*=d2r;dec*=d2r; return np.array([np.cos(dec)*np.cos(ra),np.cos(dec)*np.sin(ra),np.sin(dec)])
def to_radec(v):
    v=v/np.linalg.norm(v); return (np.degrees(np.arctan2(v[1],v[0]))%360, np.degrees(np.arcsin(v[2])))
def frame(ra,dec,pa,mirror,pasign):
    b=radec(ra,dec); z=np.array([0,0,1.])
    e=np.cross(z,b); e/=np.linalg.norm(e); n=np.cross(b,e)   # e0,n0 ; check e x n = b
    assert np.allclose(np.cross(e,n),b)
    p=pasign*pa*d2r; u=np.cos(p)*n+np.sin(p)*e
    r=np.cross(b,u)*(-1 if mirror else 1)
    return r,u,b
def axis(F1,F2,lever=0.5):
    pts=lambda F:[F[2],(F[2]+lever*F[0]),(F[2]+lever*F[1])]
    P1=[x/np.linalg.norm(x) for x in pts(F1)]; P2=[x/np.linalg.norm(x) for x in pts(F2)]
    d=[a-b for a,b in zip(P1,P2)]
    best=None
    for i,j in [(0,1),(0,2),(1,2)]:
        c=np.cross(d[i],d[j]); m=np.linalg.norm(c)
        if best is None or m>best[0]: best=(m,c)
    A=best[1]/best[0]
    return A if A[2]>0 else -A
def ang(a,b): return np.degrees(np.arccos(np.clip(a@b,-1,1)))
# --- simulate truth: mount axis A_true, camera frame fixed relative to axis
rng=np.random.default_rng(1)
true_conv=(False,+1)  # (mirror,pasign) of the solver's reported PA
def rodrigues(v,k,t): return v*np.cos(t)+np.cross(k,v)*np.sin(t)+k*(k@v)*(1-np.cos(t))
A_true=radec(37.0,89.0-0.8)  # 0.8 deg off pole
A_true/=np.linalg.norm(A_true)
# true camera frame at pos0 (physical): boresight 
b0=radec(100,40); r0=np.cross(b0,np.array([0,0,1.])); 
# build physical frame: u0 north rotated by true pa
def phys_frame(ra,dec,pa): # what the solver would see with truth conv
    return frame(ra,dec,pa,*true_conv) if False else None
# simpler: pick a physical rigid frame (R0,U0,B0) with correct handedness, rotate about A_true
B0=radec(100,40); E=np.cross([0,0,1.],B0);E/=np.linalg.norm(E);N=np.cross(B0,E)
U0=np.cos(.3)*N+np.sin(.3)*E; R0=np.cross(B0,U0)
def solver_report(R,U,B,mirror,pasign):
    # what a solver with convention (mirror,pasign) would report as (ra,dec,pa)
    ra,dec=to_radec(B)
    e=np.cross([0,0,1.],B);e/=np.linalg.norm(e);n=np.cross(B,e)
    pa=np.degrees(np.arctan2(U@e,U@n))*pasign
    return ra,dec,pa%360
res={}
for tc in itertools.product([False,True],[1,-1]):
  for noise in [0.0,0.002]:
    frames=[]
    for t in [0,40,85]:
        th=np.radians(t)
        R,U,B=[rodrigues(x,A_true/np.linalg.norm(A_true),th) for x in (R0,U0,B0)]
        if tc[0]: R=-R   # mirrored image => right axis flipped relative to normal
        ra,dec,pa=solver_report(R,U,B,tc[0],tc[1])
        ra+=rng.normal(0,noise)/np.cos(dec*d2r) ; dec+=rng.normal(0,noise); pa+=rng.normal(0,noise)
        frames.append((ra,dec,pa))
    best=None
    for conv in itertools.product([False,True],[1,-1]):
        F=[frame(*f,*conv) for f in frames]
        A=axis(F[0],F[1])
        resid=abs(ang(A,F[2][2])-ang(A,F[0][2]))
        if best is None or resid<best[0]: best=(resid,conv,A)
    ok = best[1]==tc
    print("true",tc,"noise",noise,"-> picked",best[1],"resid %.4f deg"%best[0],"axis err %.4f deg"%ang(best[2],A_true), "OK" if ok else "WRONG")

print("--- PA-sign only (mirror dropped), residual per sign, noise 0.002 (~7 arcsec), rotations 40/85 deg")
for ps_true in (1,-1):
  frames=[]
  for t in [0,40,85]:
    th=np.radians(t)
    R,U,B=[rodrigues(x,A_true,th) for x in (R0,U0,B0)]
    ra,dec,pa=solver_report(R,U,B,False,ps_true)
    frames.append((ra+rng.normal(0,.002)/np.cos(dec*d2r),dec+rng.normal(0,.002),pa+rng.normal(0,.002)))
  for ps in (1,-1):
    F=[frame(*f,False,ps) for f in frames]; A=axis(F[0],F[1])
    print("true pasign",ps_true,"try",ps,"resid %.4f"%abs(ang(A,F[2][2])-ang(A,F[0][2])),"axis err %.4f"%ang(A,A_true))
