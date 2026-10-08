import numpy as np
d2r=np.pi/180
def altaz(ra,dec,lat,lst):
    h=(lst-ra)*d2r;de=dec*d2r;la=lat*d2r
    sa=np.sin(de)*np.sin(la)+np.cos(de)*np.cos(la)*np.cos(h)
    alt=np.degrees(np.arcsin(sa)); y=-np.cos(de)*np.sin(h); x=np.sin(de)*np.cos(la)-np.cos(de)*np.sin(la)*np.cos(h)
    return alt,np.degrees(np.arctan2(y,x))%360
def altaz_to_radec(alt,az,lat,lst):
    a=alt*d2r;z=az*d2r;la=lat*d2r
    sd=np.sin(a)*np.sin(la)+np.cos(a)*np.cos(la)*np.cos(z)
    dec=np.arcsin(sd)
    # hour angle
    y=-np.sin(z)*np.cos(a); x=np.sin(a)*np.cos(la)-np.cos(a)*np.sin(la)*np.cos(z)  # sin H cos d ... derive: 
    H=np.degrees(np.arctan2(np.sin(z)*-1*np.cos(a)*-1, 1)) 
    # robust: numeric search over H
    best=None
    for Hd in np.linspace(-180,180,360001):
        aa,zz=altaz(lst-Hd,np.degrees(dec),lat,lst)
        e=abs(aa-alt)+abs(((zz-az+180)%360)-180)
        if best is None or e<best[0]: best=(e,Hd)
    return (lst-best[1])%360,np.degrees(dec)
lat,lst=35.5,123.0
for dalt,daz in [(0.3,0.0),(0.0,0.2),(-0.1,-0.25)]:
    ra,dec=altaz_to_radec(lat+dalt,daz%360,lat,lst)
    alt,az=altaz(ra,dec,lat,lst); azw=az-360 if az>180 else az
    up=(lat-alt)*60; east=-azw*np.cos(alt*d2r)*60
    print("axis offset alt %+.2f az %+.2f deg -> moveUp %+.1f' moveEast %+.1f'  total polar dist %.1f'"%(dalt,daz,up,east,(90-dec)*60))
